using System;
using _02_Scripts.Shop;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ShopElement : MonoBehaviour
{
    [Serializable]
    public struct ActiveObject
    {
        public GameObject[] Objects;
    }
    
    [SerializeField] private string id;
    [SerializeField] private int price;
    [SerializeField] private string name;
    [SerializeField] private TextMeshProUGUI NameTMP;
    
    [SerializeField] private ActiveObject[] ActiveObj = new  ActiveObject[2];
    [SerializeField] private ActiveObject[] PriceObj = new  ActiveObject[2];
    
    [SerializeField] private Button button;
    [SerializeField] private TextMeshProUGUI PriceTMP;

    private bool isActive = false;
    private bool isOpen = false;

    [SerializeField] private bool isCharacterButton = false;
    [SerializeField] private bool isCatButton = false;
    public void Init()
    {
        button?.onClick.AddListener(ClickBtn);
        PriceTMP.text = price.ToString();
        NameTMP.text = name;
        
        UpdateUI();
    }

    public void ClickBtn()
    {
        if (isOpen)
        {
                // 이미 구매된 경우: 켜기/끄기 토글
                isActive = !isActive;
                if (isActive)
                {
                    FilterHuman();
                    FilterCat();
                }
                PlayerPrefs.SetInt($"{id}_isActive", isActive ? 1 : 0);
                PlayerPrefs.Save();
                EventManager.Instance.Publish("UpdateRoom");
        }
        else
        {
                // 구매 로직
                if (GameManager.instance != null && CoinManager.TrySpendCoins(price))
                {
                    // 구매 상태 저장
                    isOpen = true;
                    FilterHuman();
                    
                    PlayerPrefs.SetInt($"{id}_isOpen", 1);
                    PlayerPrefs.SetInt($"{id}_isActive", 1); // 구매하면 자동 활성화

                    PlayerPrefs.Save();
                    EventManager.Instance.Publish("UpdateRoom");

                    isActive = true;
                }
                else
                {
                    // 돈 부족 → 무반응
                    Debug.Log("Gold가 부족합니다.");
                }
        }
        
        EventManager.Instance.Publish("UpdateUI");
    }
    public void UpdateUI()
    {
        isOpen = PlayerPrefs.HasKey($"{id}_isOpen");
        if (isOpen)
        {
            isActive = PlayerPrefs.GetInt($"{id}_isActive", 0) == 1;
            if (isActive)
            {
                foreach (GameObject obj in ActiveObj[0].Objects)
                {
                    obj.SetActive(false);
                }
                foreach (GameObject obj in ActiveObj[1].Objects)
                {
                    obj.SetActive(true);
                }
            }
            else
            {
                foreach (GameObject obj in ActiveObj[0].Objects)
                {
                    obj.SetActive(true);
                }
                foreach (GameObject obj in ActiveObj[1].Objects)
                {
                    obj.SetActive(false);
                }
            }
        }
        else
        {
            if (GameManager.instance != null && CoinManager.Balance >= price)
            {
                foreach (GameObject obj in PriceObj[0].Objects)
                {
                    obj.SetActive(false);
                }
                foreach (GameObject obj in PriceObj[1].Objects)
                {
                    obj.SetActive(true);
                }
            }
            else
            {
                foreach (GameObject obj in PriceObj[0].Objects)
                {
                    obj.SetActive(true);
                }
                foreach (GameObject obj in PriceObj[1].Objects)
                {
                    obj.SetActive(false);
                }
            }
        }
    }

    public void FilterHuman()
    {
        if (isCharacterButton)
        {
            PlayerPrefs.SetInt("Human1_isActive", 0);
            PlayerPrefs.SetInt("Human2_isActive", 0);
            PlayerPrefs.SetInt("Human3_isActive", 0);
            PlayerPrefs.SetInt("Human4_isActive", 0);
        }
    }

    public void FilterCat()
    {
        if (isCatButton)
        {
            PlayerPrefs.SetInt("Cat1_isActive", 0);
            PlayerPrefs.SetInt("Cat2_isActive", 0);
            PlayerPrefs.SetInt("Cat3_isActive", 0);
        }
    }
}
