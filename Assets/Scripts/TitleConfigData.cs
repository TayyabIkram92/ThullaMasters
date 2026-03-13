using System;
using System.Collections.Generic;

/// <summary>
/// Single JSON stored in PlayFab TitleData under key "AppConfig".
/// Replaces the old "GameModeConfig" key.
/// </summary>
[Serializable]
public class TitleConfigData
{
    public List<int> entryFees;         // game mode entry fees
    public string supportNo;            // WhatsApp support number e.g. "+923001234567"
    public string bankName;             // for BuyView
    public string accountName;          // for BuyView
    public string accountNumber;        // for BuyView
    public string discordWebhookUrl;    // for deposit/withdraw notifications
}
