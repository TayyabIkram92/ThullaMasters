using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using TMPro;

/// <summary>
/// Buy Coins screen. Bank details from TitleConfig.
/// No Newtonsoft — JSON built with string concatenation.
/// </summary>
public class BuyView : MonoBehaviour
{
    [Header("Bank Info (Legacy Text)")] [SerializeField]
    private Text bankNameTxt;

    [SerializeField] private Text accountNameTxt;
    [SerializeField] private Text accountNumberTxt;
    [SerializeField] private Button copyAccountNumberButton;

    [Header("Input")] [SerializeField] private TMP_InputField amountInputField;
    [SerializeField] private Button uploadScreenshotButton;
    [SerializeField] private Text uploadStatusTxt; // Legacy Text

    [Header("Action")] [SerializeField] private Button confirmButton;
    [SerializeField] private Button closeButton;
    [SerializeField] private Button howToBuyButton;

    [SerializeField] private string howToBuyUrl;

    private Texture2D _selectedScreenshot;
    private bool _screenshotSelected = false;

    private const int MinAmount = 500;
    private const int MaxAmount = 20000;

    private void OnEnable()
    {
        copyAccountNumberButton.onClick.AddListener(OnCopyAccountNumber);
        uploadScreenshotButton.onClick.AddListener(OnUploadScreenshot);
        confirmButton.onClick.AddListener(OnConfirmClicked);
        closeButton.onClick.AddListener(OnCloseClicked);
        howToBuyButton.onClick.AddListener(OnHowToBuyClicked);
        amountInputField.onValueChanged.AddListener(OnAmountChanged);

        PopulateBankInfo();
        ResetForm();
    }

    private void OnDisable()
    {
        copyAccountNumberButton.onClick.RemoveListener(OnCopyAccountNumber);
        uploadScreenshotButton.onClick.RemoveListener(OnUploadScreenshot);
        confirmButton.onClick.RemoveListener(OnConfirmClicked);
        closeButton.onClick.RemoveListener(OnCloseClicked);
        howToBuyButton.onClick.RemoveListener(OnHowToBuyClicked);
        amountInputField.onValueChanged.RemoveListener(OnAmountChanged);
    }

    private void OnHowToBuyClicked()
    {
        Application.OpenURL(howToBuyUrl);
    }

    private void PopulateBankInfo()
    {
        if (!PlayerDataManager.IsTitleConfigInitialized) return;
        var cfg = PlayerDataManager.TitleConfig;
        if (bankNameTxt) bankNameTxt.text = cfg.bankName ?? "";
        if (accountNameTxt) accountNameTxt.text = cfg.accountName ?? "";
        if (accountNumberTxt) accountNumberTxt.text = cfg.accountNumber ?? "";
    }

    private void ResetForm()
    {
        amountInputField.text = "";
        _screenshotSelected = false;
        _selectedScreenshot = null;
        if (uploadStatusTxt) uploadStatusTxt.text = "No File Chosen";
        confirmButton.interactable = false;
    }

    private void OnAmountChanged(string val) => ValidateForm();

    private void ValidateForm()
    {
        bool amountValid = int.TryParse(amountInputField.text, out int amount) &&
                           amount >= MinAmount && amount <= MaxAmount;
        confirmButton.interactable = amountValid && _screenshotSelected;
    }

    private void OnCopyAccountNumber()
    {
        if (!PlayerDataManager.IsTitleConfigInitialized) return;
        GUIUtility.systemCopyBuffer = PlayerDataManager.TitleConfig.accountNumber ?? "";
        EventManager.FireShowPopUp("Account number copied!");
    }

    private void OnUploadScreenshot()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        NativeGallery.GetImageFromGallery(path =>
        {
            if (string.IsNullOrEmpty(path))
            {
                EventManager.FireShowPopUp("No image selected. Please try again.");
                return;
            }

            // markTextureNonReadable: false ensures EncodeToPNG() works correctly
            _selectedScreenshot = NativeGallery.LoadImageAtPath(path, 1024, markTextureNonReadable: false);

            if (_selectedScreenshot == null)
            {
                _screenshotSelected = false;
                if (uploadStatusTxt) uploadStatusTxt.text = "No File Chosen";
                EventManager.FireShowPopUp("Failed to load image. Please select a different file.");
                ValidateForm();
                return;
            }

            _screenshotSelected = true;
            if (uploadStatusTxt) uploadStatusTxt.text = "File Selected";
            ValidateForm();
        }, "Select deposit screenshot");
#else
        _screenshotSelected = true;
        _selectedScreenshot = Texture2D.whiteTexture;
        if (uploadStatusTxt) uploadStatusTxt.text = "File Selected (Editor)";
        ValidateForm();
#endif
    }

    private void OnConfirmClicked()
    {
        if (!int.TryParse(amountInputField.text, out int amount)) return;
        if (!PlayerDataManager.IsTitleConfigInitialized) return;

        confirmButton.interactable = false;
        var cfg = PlayerDataManager.TitleConfig;

        StartCoroutine(SendToDiscord(
            heading: "DEPOSIT",
            playerId: PlayerDataManager.PlayFabId,
            playerName: PlayerDataManager.DisplayName,
            amount: amount,
            bankName: cfg.bankName ?? "",
            accountName: cfg.accountName ?? "",
            accountNumber: cfg.accountNumber ?? "",
            screenshot: _selectedScreenshot,
            webhookUrl: cfg.discordWebhookUrl,
            onComplete: () =>
            {
                EventManager.FireShowPopUp("Request submitted! Please wait for admin approval.");
                EventManager.FireHideView(ViewType.Buy);
            }));
    }

    private void OnCloseClicked()
    {
        EventManager.FireHideView(ViewType.Buy);
    }

    // ─── Discord Webhook ─────────────────────────────────────────────────────

    private IEnumerator SendToDiscord(string heading, string playerId, string playerName,
        int amount, string bankName, string accountName, string accountNumber,
        Texture2D screenshot, string webhookUrl, Action onComplete)
    {
        if (string.IsNullOrEmpty(webhookUrl))
        {
            EventManager.FireShowPopUp("Support unavailable.");
            confirmButton.interactable = true;
            yield break;
        }

        string content = "**" + heading + "**\n" +
                         "Player ID: " + playerId + "\n" +
                         "Player Name: " + playerName + "\n" +
                         "Amount: " + amount + "\n" +
                         "Bank: " + bankName + "\n" +
                         "Account Name: " + accountName + "\n" +
                         "Account Number: " + accountNumber;

        if (screenshot != null && screenshot != Texture2D.whiteTexture)
        {
            byte[] imgBytes = screenshot.EncodeToPNG();

            if (imgBytes == null || imgBytes.Length == 0)
            {
                Debug.LogWarning("[BuyView] EncodeToPNG produced no data. Falling back to text-only.");
                EventManager.FireShowPopUp("Screenshot could not be processed. Sending request without image.");
                yield return StartCoroutine(SendTextOnly(content, webhookUrl));
            }
            else
            {
                WWWForm form = new WWWForm();
                form.AddField("content", content);
                form.AddBinaryData("file", imgBytes, "screenshot.png", "image/png");

                using (var req = UnityWebRequest.Post(webhookUrl, form))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning("[BuyView] Discord image send error: " + req.error + " | Code: " +
                                         req.responseCode);
                        EventManager.FireShowPopUp("Failed to send screenshot. Please try again.");
                        confirmButton.interactable = true;
                        yield break;
                    }
                }
            }
        }
        else
        {
            yield return StartCoroutine(SendTextOnly(content, webhookUrl));
        }

        onComplete?.Invoke();
    }

    private IEnumerator SendTextOnly(string content, string webhookUrl)
    {
        // Build JSON manually — no Newtonsoft needed
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
            {
                Debug.LogWarning("[BuyView] Discord text send error: " + req.error + " | Code: " + req.responseCode);
                EventManager.FireShowPopUp("Failed to submit request. Please try again.");
                confirmButton.interactable = true;
            }
        }
    }
}