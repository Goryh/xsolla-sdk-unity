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

        /// <summary>
        /// Gets the Xsolla user ID (the <c>sub</c> claim of the user JWT) of the logged-in user. Requires
        /// <see cref="Register"/> and a connected store.
        /// </summary>
        /// <param name="completionHandler">Parameters: user ID (null on failure), error message (null on success).</param>
        public static void GetUserId(System.Action<string, string> completionHandler)
        {
            if (_store == null)
            {
                completionHandler?.Invoke(null, "The Xsolla store is not registered");
                return;
            }

            _store.StoreClient.GetAccessToken(
                onSuccess: token =>
                {
                    var userId = TryGetJwtSubject(token);
                    completionHandler?.Invoke(userId, userId == null ? "The access token has no subject" : null);
                },
                onError: error => completionHandler?.Invoke(null, error)
            );
        }

        [CanBeNull]
        internal static string TryGetJwtSubject([CanBeNull] string jwt)
        {
            try
            {
                var parts = jwt?.Split('.');
                if (parts == null || parts.Length < 2)
                    return null;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var json = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(payload));

                var match = System.Text.RegularExpressions.Regex.Match(json, "\"sub\"\\s*:\\s*\"([^\"]+)\"");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch (System.Exception)
            {
                return null;
            }
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
