#if !XSOLLA_SDK_UNITY_PURCHASING_DISABLE
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Purchasing;
using Xsolla.SDK.Common;

namespace Xsolla.SDK.UnityPurchasing
{
    /// <summary>
    /// Registers the Xsolla store with Unity IAP 5.
    /// <example>
    /// <code>
    /// XsollaPurchasingModule.Register(configuration);
    /// var controller = UnityIAPServices.StoreController(XsollaPurchasingModule.StoreName);
    /// </code>
    /// </example>
    /// </summary>
    public static class XsollaPurchasingModule
    {
        /// <summary>
        /// Store name to pass to <see cref="UnityIAPServices.StoreController"/>.
        /// </summary>
        public static string StoreName => XsollaPurchasingStore.Name;

        [CanBeNull] private static XsollaPurchasingStore _store;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnLoad()
        {
            _store = null;
            _validator = null;
        }

        /// <summary>
        /// Whether <see cref="Register"/> has been called in this session.
        /// </summary>
        public static bool IsRegistered => _store != null;

        /// <summary>
        /// Registers the Xsolla store under <see cref="StoreName"/>. Call once, before creating the
        /// <see cref="StoreController"/>; later calls are ignored.
        /// </summary>
        /// <param name="configuration">Client configuration.</param>
        /// <param name="setAsDefaultStore">Also make it the store <see cref="UnityIAPServices.StoreController"/> uses when given no name.</param>
        public static void Register(XsollaClientConfiguration configuration, bool setAsDefaultStore = false)
        {
            if (_store != null)
            {
                XsollaLogger.Warning("XsollaPurchasingModule", "Register: the Xsolla store is already registered");
                return;
            }

            XsollaLogger.SetLogLevel(configuration.logLevel);

            var store = new XsollaPurchasingStore(configuration);

            UnityIAPServices.AddNewCustomStore(new XsollaPurchasingStoreWrapper(store));
            UnityIAPServices.AddNewExtendedPurchaseService(StoreName,
                basePurchaseService => new XsollaPurchasingPurchaseService(basePurchaseService, store));

            if (setAsDefaultStore)
                UnityIAPServices.SetStoreAsDefault(StoreName);

            _store = store;
        }

        [CanBeNull] private static XsollaPurchasingStoreValidator _validator;

        /// <summary>
        /// Validator for receipts of Xsolla orders (<c>order.Info.Receipt</c>). Requires <see cref="Register"/>.
        /// </summary>
        [CanBeNull]
        public static XsollaPurchasingStoreValidator GetValidator()
        {
            if (_store == null)
                return null;

            return _validator ??= new XsollaPurchasingStoreValidator(_store.StoreClient);
        }
    }
}
#endif
