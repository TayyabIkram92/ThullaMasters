using System;
using System.Collections;
using System.Collections.Generic;
using UI;
using UnityEngine;
using UnityEngine.UI;

public class TutorialScreen : MonoBehaviour
{
    [SerializeField] private GameObject[] tutorialParts;
    [SerializeField] private Button nextButton;
    [SerializeField] private Button previousButton;
    [SerializeField] private Button closeButton;

    private int currentIndex = 0;
    
    private void OnEnable()
    {
        ResetTutorialScreen();
        nextButton.onClick.AddListener(NextButtonClicked);
        previousButton.onClick.AddListener(PreviousButtonClicked);
        closeButton.onClick.AddListener(CloseButtonClicked);
    }

    private void OnDisable()
    {
        nextButton.onClick.RemoveListener(NextButtonClicked);
        previousButton.onClick.RemoveListener(PreviousButtonClicked);
        closeButton.onClick.RemoveListener(CloseButtonClicked);
    }

    private void CloseButtonClicked()
    {
        EventManager.FireHideView(ViewType.Tutorial);
    }

    private void PreviousButtonClicked()
    {
        if (tutorialParts.Length > 1)
        {
            if (currentIndex > 0)
            {
                currentIndex -= 1;
                ShowTutorialPart(currentIndex);
                TurnButtonsOnOff(true, currentIndex >= 1);
            }
        }
    }

    private void NextButtonClicked()
    {
        if (tutorialParts.Length > 1)
        {
            if (currentIndex < tutorialParts.Length - 1)
            {
                currentIndex += 1;
                ShowTutorialPart(currentIndex);
                TurnButtonsOnOff(currentIndex < tutorialParts.Length - 1, true);
            }
        }
    }

    private void ResetTutorialScreen()
    {
        currentIndex = 0;
        ShowTutorialPart(currentIndex);
        TurnButtonsOnOff(true, false);
    }

    private void TurnButtonsOnOff(bool nextButtonOn, bool previousButtonOn)
    {
        nextButton.gameObject.SetActive(nextButtonOn);
        previousButton.gameObject.SetActive(previousButtonOn);
    }

    private void ShowTutorialPart(int partIndex)
    {
        HideAllTutorialParts();
        tutorialParts[partIndex].SetActive(true);
    }

    private void HideAllTutorialParts()
    {
        foreach (GameObject part in tutorialParts)
        {
            part.SetActive(false);
        }
    }
}