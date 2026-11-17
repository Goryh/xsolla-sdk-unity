#if !XSOLLA_SDK_UNITY_PURCHASING_DISABLE
using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;
using Xsolla.SDK.Utils;
using Xsolla.SDK.Store;
using Xsolla.SDK.Common;
using IapStore = UnityEngine.Purchasing.Extension.Store;

namespace Xsolla.SDK.UnityPurchasing
{
    /// <summary>
    /// Unity IAP 5 custom store backed by the Xsolla store client.
    /// <para/>
    /// Supports the <see cref="StoreController"/> flow: Connect, FetchProducts, FetchPurchases,
    /// PurchaseProduct, ConfirmPurchase and RestoreTransactions (the latter through
    /// <see cref="XsollaPurchasingPurchaseService"/>).
    /// <para/>
    /// Not synchronized: every callback of the store client is delivered on the Unity main thread.
    /// </summary>
    internal class XsollaPurchasingStore : IapStore
    {
        public const string Name = "XsollaStore";

        private const string Tag = "XsollaPurchasingStore";

        private readonly IXsollaStoreClient _storeClient;

        internal IXsollaStoreClient StoreClient => _storeClient;

        private readonly ISimpleFuture<XsollaClientConfiguration, string> _settingsFuture;
        [CanBeNull] private ISimpleFuture<bool, string> _initializedFuture;

        internal ConnectionState ConnectionState { get; private set; } = ConnectionState.Disconnected;

        /// <summary>
        /// Carts of purchases started through <see cref="Purchase"/>, keyed by sku, so a failure can be reported
        /// against the cart Unity IAP handed in.
        /// </summary>
        private readonly Dictionary<string, ICart> _cartBySku = new Dictionary<string, ICart>();

        /// <summary>
        /// Purchased quantity per (transaction, sku), recorded when the purchase is reported and drained when the
        /// order is confirmed. Consuming a collapsed multi-unit restore needs the purchased quantity.
        /// </summary>
        private readonly Dictionary<(string transactionId, string sku), int> _quantityByPurchase =
            new Dictionary<(string transactionId, string sku), int>();

        /// <summary>
        /// Where each (transaction, sku) stands in the consume pipeline, so a repeated confirmation of a purchase
        /// already being (or already) consumed is dropped. A failed consume drops its key so a later confirmation
        /// can retry; <see cref="ConsumeState.Done"/> is session-scoped by design.
        /// </summary>
        private readonly Dictionary<(string transactionId, string sku), ConsumeState> _consumeStateByPurchase =
            new Dictionary<(string transactionId, string sku), ConsumeState>();

        private enum ConsumeState
        {
            InFlight,
            Done
        }

        /// <summary>
        /// Every (transaction, sku) already handed to Unity IAP this session, so a duplicate delivery through the
        /// purchase callback is dropped. <see cref="FetchPurchases"/> always reports what the server still holds.
        /// </summary>
        private readonly HashSet<(string transactionId, string sku)> _deliveredPurchases =
            new HashSet<(string transactionId, string sku)>();

        public XsollaPurchasingStore(XsollaClientConfiguration configuration)
            : this(configuration, XsollaStoreClientFactory.Create()) { }

        /// <summary>
        /// Takes the store client explicitly so tests can drive the store against a stub.
        /// </summary>
        internal XsollaPurchasingStore(XsollaClientConfiguration configuration, IXsollaStoreClient storeClient)
        {
            _storeClient = storeClient;

            RunOnStartThread.Create();

            _settingsFuture = SimpleFuture.Create<XsollaClientConfiguration, string>(out var promise);
            XsollaLogger.SetLogLevel(configuration.logLevel);
            if (configuration.delayedTask != null)
                AwaitForConfiguration(configuration, promise);
            else
                promise.Complete(configuration);
        }

        private async void AwaitForConfiguration(XsollaClientConfiguration configuration, ISimplePromise<XsollaClientConfiguration, string> promise)
        {
            var mapper = await configuration.delayedTask;
            RunOnStartThread.Run(() => promise.Complete(mapper(configuration)));
        }

        #region Connect

        public override void Connect()
        {
            XsollaLogger.Debug(Tag, "Connect");

            if (_initializedFuture == null)
            {
                ConnectionState = ConnectionState.Connecting;

                _initializedFuture = SimpleFuture.Create<bool, string>(out var promise);
                _settingsFuture.OnComplete(
                    onSuccess: configuration => {
                        XsollaLogger.SetLogLevel(configuration.logLevel);

                        _storeClient.Initialize(
                            configuration,
                            onSuccess: () => XsollaLogger.Debug(Tag, "Initialize finished"),
                            onError: error => XsollaLogger.Debug(Tag, $"Initialize failed: {error}"),
                            onSuccessPurchaseProduct: OnPurchaseSucceeded,
                            onErrorPurchase: error => OnPurchaseFailed(error, null)
                        );
                        promise.Complete(true);
                    },
                    onError: error => {
                        XsollaLogger.Error(Tag, $"Initialize failed: {error}");
                        promise.CompleteWithError(error);
                    }
                );
            }

            _initializedFuture.OnComplete(
                onSuccess: _ => {
                    ConnectionState = ConnectionState.Connected;
                    ConnectCallback?.OnStoreConnectionSucceeded();
                },
                onError: error => {
                    ConnectionState = ConnectionState.Disconnected;
                    ConnectCallback?.OnStoreConnectionFailed(new StoreConnectionFailureDescription(error));
                }
            );
        }

        private bool IsConnected => ConnectionState == ConnectionState.Connected;

        #endregion

        #region Products

        public override void FetchProducts(IReadOnlyCollection<ProductDefinition> products)
        {
            XsollaLogger.Debug(Tag, "FetchProducts");

            if (!IsConnected)
            {
                ProductsCallback?.OnProductsFetchFailed(
                    new ProductFetchFailureDescription(ProductFetchFailureReason.ProviderUnavailable, "Xsolla store is not connected"));
                return;
            }

            var skus = products.Select(product => product.storeSpecificId).Distinct().ToArray();

            _storeClient.FetchProducts(skus,
                onSuccess: items => {
                    XsollaLogger.Debug(Tag, $"FetchProducts finished. Retrieved: {items.Length} ( {XsollaClientHelpers.ToJson(items)} )");

                    var descriptions = new List<ProductDescription>(items.Length);
                    foreach (var item in items)
                    {
                        descriptions.Add(new ProductDescription(
                            id: item.sku,
                            metadata: new ProductMetadata(
                                priceString: item.localizedPriceString,
                                title: item.localizedTitle,
                                description: item.localizedDescription,
                                currencyCode: item.currencyCode,
                                localizedPrice: (decimal) item.localizedPrice / 1_000_000
                            )
                        ));
                    }

                    ProductsCallback?.OnProductsFetched(descriptions);
                },
                onError: error => {
                    XsollaLogger.Error(Tag, $"FetchProducts failed: {error}");
                    ProductsCallback?.OnProductsFetchFailed(
                        new ProductFetchFailureDescription(ProductFetchFailureReason.ProductsUnavailable, error));
                }
            );
        }

        #endregion

        #region Purchases

        /// <summary>
        /// Reports what the Xsolla server still holds for the user: unconsumed consumables as
        /// <see cref="PendingOrder"/>s and owned non-consumables as <see cref="ConfirmedOrder"/>s.
        /// </summary>
        public override void FetchPurchases()
        {
            XsollaLogger.Debug(Tag, "FetchPurchases");

            if (!IsConnected)
            {
                PurchaseFetchCallback?.OnPurchasesRetrievalFailed(
                    new PurchasesFetchFailureDescription(PurchasesFetchFailureReason.StoreNotConnected, "Xsolla store is not connected"));
                return;
            }

            _storeClient.RestorePurchases(
                onSuccess: items => {
                    XsollaLogger.Debug(Tag, $"FetchPurchases finished. Restored: {items.Length}");

                    var orders = new List<Order>(items.Length);
                    foreach (var item in items)
                    {
                        var order = CreateFetchedOrder(item);
                        if (order != null)
                            orders.Add(order);
                    }

                    PurchaseFetchCallback?.OnAllPurchasesRetrieved(orders);
                },
                onError: error => {
                    XsollaLogger.Error(Tag, $"FetchPurchases failed: {error}");
                    PurchaseFetchCallback?.OnPurchasesRetrievalFailed(
                        new PurchasesFetchFailureDescription(PurchasesFetchFailureReason.Unknown, error));
                }
            );
        }

        [CanBeNull]
        private Order CreateFetchedOrder(XsollaStoreClientPurchasedProduct item)
        {
            var product = FindProduct(item.sku);
            if (product == null)
            {
                XsollaLogger.Warning(Tag, $"FetchPurchases: skipping sku={item.sku}, it is not among the fetched products");
                return null;
            }

            var isTracked = !string.IsNullOrEmpty(item.transactionId);
            var key = (item.transactionId, item.sku);

            // The cart item carries the owned units of the row (a collapsed restore reports several in one row).
            var cart = new Cart(new CartItem(product, item.quantity > 0 ? item.quantity : 1));

            if (product.definition.type == ProductType.Consumable)
            {
                // Already confirmed (or being consumed) in this session: the server just has not caught up yet.
                if (isTracked && _consumeStateByPurchase.ContainsKey(key))
                    return null;

                if (isTracked)
                {
                    _quantityByPurchase[key] = item.quantity > 0 ? item.quantity : 1;
                    _deliveredPurchases.Add(key);
                }

                return new PendingOrder(cart, CreateOrderInfo(item));
            }

            if (isTracked)
                _deliveredPurchases.Add(key);

            return new ConfirmedOrder(cart, CreateOrderInfo(item));
        }

        public override void Purchase(ICart cart)
        {
            var item = cart.Items().FirstOrDefault();
            var definition = GetDefinition(item);
            if (definition == null)
            {
                PurchaseCallback?.OnPurchaseFailed(new FailedOrder(cart, PurchaseFailureReason.ProductUnavailable, "The cart holds no known product"));
                return;
            }

            var sku = definition.storeSpecificId;
            XsollaLogger.Debug(Tag, $"Purchase: sku={sku}");

            _cartBySku[sku] = cart;

            _storeClient.PurchaseProduct(sku, null, XsollaStoreClientPurchaseArgs.Empty,
                onSuccess: OnPurchaseSucceeded,
                onError: error => OnPurchaseFailed(error, sku)
            );
        }

        private void OnPurchaseSucceeded(XsollaStoreClientPurchasedProduct purchased)
        {
            XsollaLogger.Debug(Tag, $"Purchase finished: {purchased}");

            var key = (purchased.transactionId, purchased.sku);
            var isTracked = !string.IsNullOrEmpty(purchased.transactionId);

            var product = FindProduct(purchased.sku);
            if (product == null)
            {
                // Not marked as delivered: the next FetchPurchases (after products are fetched) reports it.
                XsollaLogger.Warning(Tag, $"Purchase of sku={purchased.sku} reported before its product was fetched; it will surface on the next FetchPurchases");
                _cartBySku.Remove(purchased.sku);
                return;
            }

            if (isTracked && !_deliveredPurchases.Add(key))
            {
                XsollaLogger.Warning(Tag, $"Duplicate delivery ignored: transactionId={purchased.transactionId} sku={purchased.sku}");
                _cartBySku.Remove(purchased.sku);
                return;
            }

            if (isTracked)
                _quantityByPurchase[key] = purchased.quantity > 0 ? purchased.quantity : 1;

            var cart = _cartBySku.TryGetValue(purchased.sku, out var startedCart) ? startedCart : new Cart(product);
            _cartBySku.Remove(purchased.sku);

            PurchaseCallback?.OnPurchaseSucceeded(new PendingOrder(cart, CreateOrderInfo(purchased)));
        }

        private void OnPurchaseFailed(string error, [CanBeNull] string sku)
        {
            var parsed = XsollaStoreClientHelpers.ParsePurchaseError(error);
            var reason = parsed.code == XsollaStoreClientPurchaseErrorCode.Cancelled
                ? PurchaseFailureReason.UserCancelled
                : PurchaseFailureReason.PaymentDeclined;

            if (reason == PurchaseFailureReason.UserCancelled)
                XsollaLogger.Warning(Tag, $"Purchase failed: {parsed.message}, reason: {reason}, sku: {sku}");
            else
                XsollaLogger.Error(Tag, $"Purchase failed: {parsed.message}, reason: {reason}, sku: {sku}");

            if (sku == null) return;

            ICart cart;
            if (_cartBySku.TryGetValue(sku, out var startedCart))
            {
                cart = startedCart;
                _cartBySku.Remove(sku);
            }
            else
            {
                var product = FindProduct(sku);
                if (product == null) return;
                cart = new Cart(product);
            }

            PurchaseCallback?.OnPurchaseFailed(new FailedOrder(cart, reason, parsed.message));
        }

        public override void FinishTransaction(PendingOrder pendingOrder)
        {
            var transactionId = pendingOrder.Info.TransactionID;

            foreach (var item in pendingOrder.CartOrdered.Items())
            {
                var definition = GetDefinition(item);
                if (definition != null)
                    Consume(definition, transactionId);
            }

            ConfirmCallback?.OnConfirmOrderSucceeded(transactionId);
        }

        private void Consume(ProductDefinition definition, string transactionId)
        {
            var sku = definition.storeSpecificId;
            var purchase = (transactionId, sku);

            // An empty transaction id cannot be tracked, so such a confirmation consumes one unit unguarded.
            var isTracked = !string.IsNullOrEmpty(transactionId);

            if (definition.type != ProductType.Consumable)
            {
                XsollaLogger.Debug(Tag, $"FinishTransaction: sku={sku} transactionId={transactionId} type={definition.type} — not consumable, nothing to consume.");
                if (isTracked)
                    _quantityByPurchase.Remove(purchase);
                return;
            }

            if (isTracked && _consumeStateByPurchase.TryGetValue(purchase, out var consumeState))
            {
                XsollaLogger.Debug(Tag, $"FinishTransaction: ignoring a duplicate finish (state={consumeState}) sku={sku} transactionId={transactionId}");
                return;
            }

            var quantity = isTracked && _quantityByPurchase.TryGetValue(purchase, out var tracked) ? tracked : 1;

            XsollaLogger.Debug(Tag, $"FinishTransaction: consuming sku={sku} quantity={quantity} transactionId={transactionId}");

            if (isTracked)
                _consumeStateByPurchase[purchase] = ConsumeState.InFlight;

            _storeClient.ConsumeProduct(sku, quantity, transactionId,
                onSuccess: () => {
                    if (isTracked)
                    {
                        _consumeStateByPurchase[purchase] = ConsumeState.Done;
                        _quantityByPurchase.Remove(purchase);
                    }

                    XsollaLogger.Debug(Tag, $"FinishTransaction finished: sku={sku} consumed {quantity} unit(s).");
                },
                onError: error => {
                    // Release the in-flight mark so a later confirmation (or the next session) can retry.
                    if (isTracked)
                        _consumeStateByPurchase.Remove(purchase);

                    XsollaLogger.Error(Tag, $"FinishTransaction failed: sku={sku} quantity={quantity} transactionId={transactionId}: {error}");
                }
            );
        }

        public override void CheckEntitlement(ProductDefinition product)
        {
            EntitlementCallback?.OnCheckEntitlement(product, EntitlementStatus.Unknown, "CheckEntitlement is not supported by the Xsolla store");
        }

        /// <summary>
        /// Re-delivers the unconsumed consumables the server still holds through the purchase callback
        /// (<see cref="StoreController.OnPurchasePending"/>). Purchases already handed to Unity IAP this session
        /// are skipped. Owned non-consumables are not re-delivered: the restored rows have no stable transaction
        /// id, so they would look like new purchases; <see cref="FetchPurchases"/>, which follows the restore,
        /// reports them as confirmed orders instead.
        /// </summary>
        internal void RestoreTransactions(Action<bool, string> callback)
        {
            XsollaLogger.Debug(Tag, "RestoreTransactions");

            if (!IsConnected)
            {
                callback?.Invoke(false, "Xsolla store is not connected");
                return;
            }

            _storeClient.RestorePurchases(
                onSuccess: items => {
                    foreach (var item in items)
                    {
                        var product = FindProduct(item.sku);
                        if (product != null && product.definition.type != ProductType.Consumable)
                            continue;

                        OnPurchaseSucceeded(item);
                    }

                    callback?.Invoke(true, null);
                },
                onError: error => callback?.Invoke(false, error)
            );
        }

        #endregion

        #region Helpers

        [CanBeNull]
        private Product FindProduct(string sku)
        {
            if (string.IsNullOrEmpty(sku))
                return null;

            return ReadOnlyProductCache.FindByStoreSpecificId(sku) ?? ReadOnlyProductCache.Find(sku);
        }

        [CanBeNull]
        private static ProductDefinition GetDefinition([CanBeNull] CartItem item)
        {
            if (item?.Product == null)
                return null;

            return item.Product.catalogListings.TryGetValue(item.CatalogListingId, out var listing)
                ? listing.definition
                : item.Product.definition;
        }

        private static IOrderInfo CreateOrderInfo(XsollaStoreClientPurchasedProduct purchased)
        {
            var receipt = JsonUtility.ToJson(new UnifiedReceipt {
                Payload = purchased.ToReceipt().ToJson(),
                Store = Name,
                TransactionID = purchased.transactionId ?? string.Empty
            });

            var reported = purchased.purchaseTimeMillis;
            DateTime? purchaseDate = reported > 0 && reported <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                ? DateTimeOffset.FromUnixTimeMilliseconds(reported).UtcDateTime
                : (DateTime?) null;

            return new XsollaPurchasingOrderInfo(receipt, purchased.transactionId, purchaseDate);
        }

        #endregion
    }

    /// <summary>
    /// <see cref="IOrderInfo"/> of an Xsolla order. <see cref="Receipt"/> is a Unity IAP unified receipt whose
    /// <c>Store</c> is <see cref="XsollaPurchasingStore.Name"/> and whose <c>Payload</c> is the Xsolla receipt JSON.
    /// </summary>
    internal sealed class XsollaPurchasingOrderInfo : IOrderInfo
    {
        public XsollaPurchasingOrderInfo(string receipt, [CanBeNull] string transactionId, DateTime? purchaseDate)
        {
            Receipt = receipt;
            TransactionID = transactionId ?? string.Empty;
            PurchaseDate = purchaseDate;
        }

        public IAppleOrderInfo Apple => null;
        public IGoogleOrderInfo Google => null;
        public IPaymentProvidersOrderInfo PaymentProviders => null;
        public List<IPurchasedProductInfo> PurchasedProductInfo { get; set; } = new List<IPurchasedProductInfo>();
        public string Receipt { get; }
        public string TransactionID { get; }
        public DateTime? PurchaseDate { get; }
    }

    /// <summary>
    /// Registers <see cref="XsollaPurchasingStore"/> with Unity IAP under <see cref="XsollaPurchasingStore.Name"/>.
    /// </summary>
    internal sealed class XsollaPurchasingStoreWrapper : IStoreWrapper
    {
        private readonly XsollaPurchasingStore _store;

        public XsollaPurchasingStoreWrapper(XsollaPurchasingStore store) => _store = store;

        public IapStore instance => _store;
        public string name => XsollaPurchasingStore.Name;
        public ConnectionState GetStoreConnectionState() => _store.ConnectionState;
    }

    /// <summary>
    /// Purchase service of the Xsolla store: the default Unity IAP purchase service plus RestoreTransactions,
    /// which the default service reports as unsupported for custom stores.
    /// </summary>
    internal sealed class XsollaPurchasingPurchaseService : ExtensiblePurchaseService
    {
        private readonly XsollaPurchasingStore _store;

        public XsollaPurchasingPurchaseService(IPurchaseService basePurchaseService, XsollaPurchasingStore store)
            : base(basePurchaseService)
        {
            _store = store;
        }

        public override void RestoreTransactions(Action<bool, string> callback)
        {
            _store.RestoreTransactions((success, error) => {
                if (success)
                    FetchPurchases();
                callback?.Invoke(success, error);
            });
        }
    }
}
#endif
