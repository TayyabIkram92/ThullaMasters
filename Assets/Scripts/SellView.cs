using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using TMPro;

public class SellView : MonoBehaviour
{
    [Header("Form")] [SerializeField] private Dropdown bankDropdown;
    [SerializeField] private TMP_InputField accountNumberInput;
    [SerializeField] private TMP_InputField accountNameInput;
    [SerializeField] private TMP_InputField amountInput;

    [Header("Action")] [SerializeField] private Button confirmButton;
    [SerializeField] private Button closeButton;

    private const int MinWithdraw = 1000;
    private const int MaxWithdraw = 20000;

    int currentCoins = 0;

    private readonly string[] _banks = new[]
    {
        "Select Bank",
        "Easypaisa", "JazzCash", "SadaPay", "NayaPay", "NoBank"
    };

    private void OnEnable()
    {
        EventManager.FireGetCoinsRequested(coins =>
            currentCoins = coins);
        confirmButton.onClick.AddListener(OnConfirmClicked);
        closeButton.onClick.AddListener(OnCloseClicked);
        bankDropdown.onValueChanged.AddListener(_ => ValidateForm());
        accountNumberInput.onValueChanged.AddListener(_ => ValidateForm());
        accountNameInput.onValueChanged.AddListener(_ => ValidateForm());
        amountInput.onValueChanged.AddListener(_ => ValidateForm());

        ResetForm();
    }

    private void OnDisable()
    {
        confirmButton.onClick.RemoveListener(OnConfirmClicked);
        closeButton.onClick.RemoveListener(OnCloseClicked);
        bankDropdown.onValueChanged.RemoveAllListeners();
        accountNumberInput.onValueChanged.RemoveAllListeners();
        accountNameInput.onValueChanged.RemoveAllListeners();
        amountInput.onValueChanged.RemoveAllListeners();
    }

    private void ResetForm()
    {
        bankDropdown.ClearOptions();
        bankDropdown.AddOptions(new List<string>(_banks));
        bankDropdown.value = 0;
        accountNumberInput.text = "";
        accountNameInput.text = "";
        amountInput.text = "";
        confirmButton.interactable = false;
    }

    private void ValidateForm()
    {
        bool bankOk = bankDropdown.value > 0;
        bool accNumOk = !string.IsNullOrWhiteSpace(accountNumberInput.text);
        bool accNameOk = !string.IsNullOrWhiteSpace(accountNameInput.text);
        bool amountOk =
            int.TryParse(amountInput.text, out int amt) && amt >= MinWithdraw &&
            amt <= MaxWithdraw && amt <= currentCoins;
        confirmButton.interactable = bankOk && accNumOk && accNameOk && amountOk;
    }

    private void OnConfirmClicked()
    {
        if (!PlayerDataManager.IsTitleConfigInitialized) return;
        if (!int.TryParse(amountInput.text, out int amount)) return;

        confirmButton.interactable = false;
        string webhookUrl = PlayerDataManager.TitleConfig?.discordWebhookUrl ?? "";

        StartCoroutine(SendWithdrawRequest(
            playerId: PlayerDataManager.PlayFabId,
            playerName: PlayerDataManager.DisplayName,
            bank: _banks[bankDropdown.value],
            accountNumber: accountNumberInput.text.Trim(),
            accountName: accountNameInput.text.Trim(),
            amount: amount,
            webhookUrl: webhookUrl));
    }

    private IEnumerator SendWithdrawRequest(string playerId, string playerName,
        string bank, string accountNumber, string accountName, int amount, string webhookUrl)
    {
        if (string.IsNullOrEmpty(webhookUrl))
        {
            EventManager.FireShowPopUp("Withdraw unavailable.");
            confirmButton.interactable = true;
            yield break;
        }

        string content = "**WITHDRAW**\n" +
                         "Player ID: " + playerId + "\n" +
                         "Player Name: " + playerName + "\n" +
                         "Amount: " + amount + "\n" +
                         "Bank: " + bank + "\n" +
                         "Account Name: " + accountName + "\n" +
                         "Account Number: " + accountNumber;

        string escapedContent = content
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
        string jsonBody = "{\"content\":\"" + escapedContent + "\"}";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

        using (var req = new UnityWebRequest(webhookUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bodyBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning("[SellView] Discord error: " + req.error);
        }

        EventManager.FireShowPopUp("Withdraw request submitted!");
        EventManager.FireHideView(ViewType.Sell);
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.Sell);
    }
}