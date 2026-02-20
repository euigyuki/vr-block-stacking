using TMPro;
using UnityEngine;
using Unity.Netcode;

public class ScoreManager : NetworkBehaviour
{
    public TextMeshProUGUI scoreText;

    private NetworkVariable<int> currentStackedCount = new NetworkVariable<int>();
    public int CurrentScore => currentStackedCount.Value;
    public static ScoreManager Instance { get; private set; }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }
    public override void OnNetworkSpawn()
    {
        currentStackedCount.OnValueChanged += OnScoreChanged;
        UpdateScoreUI(currentStackedCount.Value);
    }

    public override void OnNetworkDespawn()
    {
        currentStackedCount.OnValueChanged -= OnScoreChanged;
    }

    private void OnEnable()
    {
        StackableBlock.OnStackedStateChanged += HandleScoreChange;
    }

    private void OnDisable()
    {
        StackableBlock.OnStackedStateChanged -= HandleScoreChange;
    }

    private void HandleScoreChange(StackableBlock block, bool isStacked)
    {
        if (!IsServer) return; // only host updates the score

        if (isStacked) currentStackedCount.Value++;
        else currentStackedCount.Value = Mathf.Max(0, currentStackedCount.Value - 1);
    }

    private void OnScoreChanged(int oldValue, int newValue)
    {
        UpdateScoreUI(newValue);
    }

    private void UpdateScoreUI(int value)
    {
        if (scoreText != null)
            scoreText.text = "Live Score: " + value;
    }
}