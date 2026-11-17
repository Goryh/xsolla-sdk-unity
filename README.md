# Xsolla Mobile SDK for Unity

![License](https://img.shields.io/github/license/xsolla/xsolla-sdk-unity)
![Latest release](https://img.shields.io/github/v/release/xsolla/xsolla-sdk-unity)
[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-orange.svg)](https://unity.com)
[![C#](https://img.shields.io/badge/C%23-blue.svg)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![UPM compatible](https://img.shields.io/badge/UPM-compatible-brightgreen.svg)](https://docs.unity3d.com/Manual/upm-ui.html)

Pre-built Unity SDK for integrating in-game payments into your app via Xsolla Pay Station.

## SDK Explorer

See exactly how payments work before writing a single line of code. The SDK Explorer lets you walk through authentication, catalog loading, purchasing, and finalization — all in an interactive environment.

[![SDK Explorer — interactive demo of Xsolla Mobile SDK payment flow](readme-assets~/explorer.png)](https://developers.xsolla.com/sdk/demo/)

[**Integrate Now →**](https://developers.xsolla.com/sdk/demo/)

## Essential Links

- [SDK Explorer](https://developers.xsolla.com/sdk/demo/) — interactive demo
- [SDK Documentation](https://developers.xsolla.com/sdk/) — full integration guide
- [Demo App](https://github.com/xsolla/xsolla-sdk-demo) — sample project

## Overview

Xsolla Mobile SDK provides a Unity IAP-compatible purchasing module for in-game purchases via Xsolla Pay Station. It registers as a Unity IAP 5 custom store and works with the standard `StoreController` flow so the purchase flow feels familiar to Unity developers.

**Key features:**

- 1000+ payment methods across 200+ geographies
- 130+ currencies including local and alternative payment methods
- Built-in anti-fraud protection
- 25+ languages supported out of the box
- Player authentication (Xsolla Login widget, social login, custom tokens)
- Product catalog and virtual items
- Buy Button and Web Shop integration

## Requirements

- Unity 2022.3 LTS or later
- Unity IAP (`com.unity.purchasing`) 5.5.1+ (4.x is no longer supported)

## Installation

Add the package via Unity Package Manager using the Git URL:

1. In Unity, go to **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter:
   ```
   https://github.com/xsolla/xsolla-sdk-unity.git
   ```
4. Click **Add**

## Quick Start

### 1. Connect

Configure the SDK with your project credentials, register the Xsolla store with Unity IAP 5 and connect a `StoreController` to it:

```csharp
using UnityEngine;
using UnityEngine.Purchasing;
using Xsolla.SDK.Common;
using Xsolla.SDK.UnityPurchasing;

var settings = XsollaClientSettings.Builder.Create()
    .SetProjectId(77640)
    .SetLoginId("026201e3-7e40-11ea-a85b-42010aa80004")
    .Build();

var configuration = XsollaClientConfiguration.Builder.Create()
    .SetSettings(settings)
    .SetSandbox(true)
    .Build();

XsollaPurchasingModule.Register(configuration); // once per session

var controller = UnityIAPServices.StoreController(XsollaPurchasingModule.StoreName);
controller.OnPurchasePending += OnPurchasePending;
controller.OnPurchaseFailed += failedOrder => { /* handle error */ };
controller.OnProductsFetched += products => controller.FetchPurchases();
controller.OnPurchasesFetched += orders => { /* ConfirmedOrders: owned non-consumables, PendingOrders: unconsumed consumables */ };

await controller.Connect();

var catalog = new CatalogProvider();
catalog.AddProduct("com.xsolla.crystals.10", ProductType.Consumable);
// ...more products
catalog.FetchProducts(controller.FetchProductsWithNoRetries);
```

### 2. Purchase

```csharp
controller.PurchaseProduct("com.xsolla.crystals.10");
```

### 3. Finalize

Validate the receipt of a pending order, award the product and confirm it (consumables are consumed on confirmation):

```csharp
void OnPurchasePending(PendingOrder order)
{
    XsollaPurchasingModule.GetValidator().Validate(order.Info.Receipt, (success, error) =>
    {
        if (success)
        {
            // Award the product to the user
            controller.ConfirmPurchase(order);
        }
    });
}
```

`CrossPlatformValidator` does not understand Xsolla receipts; use `XsollaPurchasingStoreValidator.IsXsollaReceipt(order.Info.Receipt)` to route them to the Xsolla validator.

`controller.RestoreTransactions(...)` re-delivers purchases the server still holds through `OnPurchasePending`, then fetches purchases again.

> For the full integration guide, see the [SDK Documentation](https://developers.xsolla.com/sdk/).

## Support

- **GitHub Issues:** [github.com/xsolla/xsolla-sdk-unity/issues](https://github.com/xsolla/xsolla-sdk-unity/issues)
- **Developer portal:** [developers.xsolla.com](https://developers.xsolla.com)

## License

Apache 2.0 License. See [LICENSE](./LICENSE).