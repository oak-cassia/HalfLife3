using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using _02_Scripts.Http.Components;
using _02_Scripts.Http; // GithubService를 위해 추가
using _02_Scripts.Reward;
using UnityEngine;

/// <summary>
/// DailyQuestManager
/// - 매일 08:00(로컬) 트랙당 1개 퀘스트 자동 생성(00:00으로 자동생성 시간을 변경하는게 좋을까요)
/// - 완료 조건: 버튼 클릭(검증 없음)
/// - 저장: 날짜별 JSON (Application.persistentDataPath/DailyQuests/)
/// - UI/네트워크와 분리: 이벤트(Event) + 퍼블릭 API만 제공
/// </summary>
public sealed class DailyQuestManager : MonoBehaviour
{
    public static DailyQuestManager Instance { get; private set; }

    [Header("Active Tracks (인스펙터에서 원하는 만큼 추가 가능)")] [SerializeField]
    private List<TrackType> activeTracks = new(); // 인스펙터에서 선택

    [Header("Generation Time (Local)")] [Range(0, 23)]
    public int generateHour = 8; // 08:00

    [Header("GitHub 자동 검증 설정 (Portfolio 퀘스트용)")] [SerializeField]
    private string githubToken = ""; // GitHub PAT

    [SerializeField] private string githubUserAgent = "HalfLife3-DailyQuest/1.0";
    [SerializeField] private string githubOwner = "oak-cassia";
    [SerializeField] private string githubRepo = "HalfLife3";
    [SerializeField] private string githubBranch = ""; // 빈 값이면 default branch 사용
    [SerializeField] private bool enableAutoVerification = true; // 자동 검증 활성화
    [SerializeField] private float verificationInterval = 5f; // 5초마다 검증
    [SerializeField] private bool useLocalTime; // true: 로컬 시간 기준, false: UTC 기준

    [Header("Dependencies")] [SerializeField]
    private RewardManager rewardManager; // Unity에서 주입할 RewardManager

    // 이벤트 — UI가 구독해서 갱신
    public event Action<IReadOnlyList<QuestData>> OnQuestsGenerated;
    public event Action<QuestData> OnQuestCompleted;
    public event Action OnPerfectDay;

    private readonly List<QuestData> _todayQuests = new();
    private DailySave _save = new DailySave();
    private string _todayStr;
    private DateTime _nextGenTimeLocal;

    // GitHub 자동 검증 관련
    private GithubService _githubService;
    private Coroutine _verificationCoroutine;
    private DateTimeOffset _githubSettingsUpdatedTime; // GitHub 설정 업데이트 시점 저장

    private string BasePath =>
        Path.Combine(Application.persistentDataPath, "DailyQuests");

    // ===== Unity lifecycle =====
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        Directory.CreateDirectory(BasePath);

        // DateManager 초기화 (전역 날짜 관리)
        var dateManager = DateManager.Instance;
        Debug.Log($"[DailyQuestManager] DateManager 초기화 - 현재 날짜: {dateManager.GetCurrentDateString()}, 새로운 날: {dateManager.IsNewDay}");

        // DateManager의 날짜를 사용
        _todayStr = dateManager.GetCurrentDateString();

        // 트랙 선택을 위해 activeTracks 초기화 (인스펙터 설정 무시)
        activeTracks.Clear();
        Debug.Log("[DailyQuestManager] activeTracks 초기화 완료 - 트랙 선택 대기");

        LoadOrInit();
        SetupNextGenTime();
        StartCoroutine(Scheduler());

        UpdateGitHubSettingsAsync(githubToken, githubUserAgent,  githubOwner, githubRepo);
        StartGitHubVerification();
    }

    // ===== Public API =====

    /// <summary>
    /// GitHub 자동 검증을 시작합니다.
    /// 외부에서 GitHub 설정이 완료된 후 호출해야 합니다.
    /// </summary>
    public bool StartGitHubVerification()
    {
        // 이미 실행 중이면 반환
        if (_verificationCoroutine != null)
        {
            Debug.Log("[DailyQuestManager] GitHub 자동 검증이 이미 실행 중입니다.");
            return true;
        }

        // 자동 검증이 비활성화되어 있으면 반환
        if (!enableAutoVerification)
        {
            Debug.Log("[DailyQuestManager] GitHub 자동 검증이 비활성화되어 있습니다.");
            return false;
        }

        // GitHub 설정이 완료되지 않았으면 반환
        if (string.IsNullOrEmpty(githubToken) || string.IsNullOrEmpty(githubOwner) || string.IsNullOrEmpty(githubRepo))
        {
            Debug.LogWarning("[DailyQuestManager] GitHub 설정이 완료되지 않아 자동 검증을 시작할 수 없습니다. (token, owner, repo 필요)");
            return false;
        }

        try
        {
            var githubClient = new GithubClient(githubToken, githubUserAgent);
            _githubService = new GithubService(githubClient);
            Debug.Log("[DailyQuestManager] GitHub 서비스 초기화 완료");

            // 자동 검증 코루틴 시작
            _verificationCoroutine = StartCoroutine(AutoVerifyPortfolioQuests());
            Debug.Log($"[DailyQuestManager] GitHub 자동 검증 시작 - {verificationInterval}초마다 확인");

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DailyQuestManager] GitHub 자동 검증 시작 중 오류 발생: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// GitHub 자동 검증을 중지합니다.
    /// </summary>
    public void StopGitHubVerification()
    {
        if (_verificationCoroutine != null)
        {
            StopCoroutine(_verificationCoroutine);
            _verificationCoroutine = null;
            Debug.Log("[DailyQuestManager] GitHub 자동 검증이 중지되었습니다.");
        }
    }

    /// <summary>
    /// GitHub 설정을 동적으로 업데이트합니다.
    /// </summary>
    public void UpdateGitHubSettings(string token, string owner, string repo, string branch = "")
    {
        // 기존 검증 중지
        StopGitHubVerification();

        // 설정 업데이트
        githubToken = token;
        githubOwner = owner;
        githubRepo = repo;
        githubBranch = branch;

        // GitHub 설정 업데이트 시점 저장
        _githubSettingsUpdatedTime = DateTimeOffset.Now;

        Debug.Log($"[DailyQuestManager] GitHub 설정 업데이트 완료 - Owner: {owner}, Repo: {repo}");

        // 자동으로 검증 시작 (설정이 유효하면)
        StartGitHubVerification();
    }

    /// <summary>
    /// GitHub 설정을 동적으로 업데이트하고 저장소 유효성을 검증합니다.
    /// </summary>
    /// <returns>저장소가 유효하고 접근 가능하면 true, 그렇지 않으면 false</returns>
    public async System.Threading.Tasks.Task<bool> UpdateGitHubSettingsAsync(string token, string owner, string repo, string branch = "")
    {
        // 기존 검증 중지
        StopGitHubVerification();

        // 설정 임시 저장 (검증 성공 시에만 실제 적용)
        var originalToken = githubToken;
        var originalOwner = githubOwner;
        var originalRepo = githubRepo;
        var originalBranch = githubBranch;

        try
        {
            // 임시로 설정 적용하여 검증
            githubToken = token;
            githubOwner = owner;
            githubRepo = repo;
            githubBranch = branch;

            Debug.Log($"[DailyQuestManager] GitHub 저장소 검증 시작 - Owner: {owner}, Repo: {repo}");

            // GitHub 클라이언트로 저장소 유효성 검증
            var githubClient = new GithubClient(token, githubUserAgent);
            var tempGithubService = new GithubService(githubClient);

            var (exists, isPrivate, defaultBranch) = await tempGithubService.GetRepositoryMetaAsync(
                owner,
                repo,
                CancellationToken.None
            );

            if (!exists)
            {
                Debug.LogError($"[DailyQuestManager] GitHub 저장소를 찾을 수 없습니다: {owner}/{repo}");
                // 원래 설정으로 복구
                githubToken = originalToken;
                githubOwner = originalOwner;
                githubRepo = originalRepo;
                githubBranch = originalBranch;
                return false;
            }

            // 브랜치가 지정되지 않았으면 기본 브랜치 사용
            if (string.IsNullOrEmpty(branch) && !string.IsNullOrEmpty(defaultBranch))
            {
                githubBranch = defaultBranch;
                Debug.Log($"[DailyQuestManager] 기본 브랜치 사용: {defaultBranch}");
            }

            // GitHub 설정 업데이트 시점 저장
            _githubSettingsUpdatedTime = DateTimeOffset.Now;
            Debug.Log($"[DailyQuestManager] GitHub 설정 업데이트 시점 저장: {_githubSettingsUpdatedTime:yyyy-MM-dd HH:mm:ss}");

            Debug.Log($"[DailyQuestManager] GitHub 저장소 검증 성공 - Owner: {owner}, Repo: {repo}, Private: {isPrivate}, Branch: {githubBranch}");

            // 검증 성공 시 자동으로 검증 시작
            var started = StartGitHubVerification();
            if (started)
            {
                Debug.Log("[DailyQuestManager] GitHub 자동 검증이 시작되었습니다.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DailyQuestManager] GitHub 저장소 검증 중 오류 발생: {ex.Message}");

            // 오류 발생 시 원래 설정으로 복구
            githubToken = originalToken;
            githubOwner = originalOwner;
            githubRepo = originalRepo;
            githubBranch = originalBranch;

            return false;
        }
    }

    public IReadOnlyList<QuestData> GetQuests()
    {
        // 디버깅을 위해 일시적으로 로그 활성화
        Debug.Log($"[DailyQuestManager] GetQuests() 호출됨 - 현재 퀘스트 개수: {_todayQuests.Count}");
        for (int i = 0; i < _todayQuests.Count; i++)
        {
            Debug.Log($"[DailyQuestManager] Quest[{i}] - ID: {_todayQuests[i].id}, Status: {_todayQuests[i].status}");
        }

        return _todayQuests;
    }

    /// <summary>완료(검증 없음, 버튼으로 호출)</summary>
    public bool CompleteQuest(string questId)
    {
        var q = _todayQuests.Find(x => x.id == questId);
        if (q == null)
        {
            Debug.LogError($"[DailyQuestManager] 퀘스트를 찾을 수 없습니다: {questId}");
            return false;
        }

        if (q.status == QuestStatus.Completed)
        {
            Debug.LogWarning($"[DailyQuestManager] 이미 완료된 퀘스트입니다: {questId}");
            return false;
        }

        // 퀘스트 완료 처리
        q.status = QuestStatus.Completed;

        // RewardManager에 Daily 보상 카운트 증가
        if (rewardManager != null)
        {
            rewardManager.Increase(RewardType.Daily, 1);
            Debug.Log($"[DailyQuestManager] RewardManager Daily 카운트 증가 완료 - Quest ID: {questId}");
        }
        else
        {
            Debug.LogWarning("[DailyQuestManager] RewardManager가 할당되지 않았습니다!");
        }

        // 이벤트 발생 (UI 업데이트용)
        try
        {
            if (OnQuestCompleted != null)
            {
                OnQuestCompleted.Invoke(q);
                // 추가 안전성을 위해 다음 프레임에서도 한 번 더 이벤트 발생
                StartCoroutine(InvokeQuestCompletedNextFrame(q));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DailyQuestManager] OnQuestCompleted 이벤트 발생 중 오류: {ex.Message}");
        }

        // 모두 완료 시 Perfect Day
        bool allDone = true;
        foreach (var it in _todayQuests)
            if (it.status != QuestStatus.Completed)
            {
                allDone = false;
                break;
            }

        if (allDone)
        {
            _save.streak += 1;
            OnPerfectDay?.Invoke();
        }

        SaveToday();
        return true;
    }

    public DateTime GetNextGenerationTimeLocal() => _nextGenTimeLocal;

    /// <summary>퀘스트 초기화 (모든 퀘스트 삭제 및 저장 파일 제거)</summary>
    public void ClearAllQuests()
    {
        _todayQuests.Clear();

        // 저장 파일도 삭제
        var path = PathFor(_todayStr);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        // 빈 저장 데이터로 초기화
        _save = new DailySave
        {
            date = _todayStr,
            quests = new QuestData[0],
            streak = 0
        };

        // UI에 빈 목록 알림
        OnQuestsGenerated?.Invoke(_todayQuests);
    }

    /// <summary>활성 트랙 설정 (트랙 선택 UI에서 호출)</summary>
    public void SetActiveTracks(List<TrackType> selectedTracks)
    {
        Debug.Log($"[DailyQuestManager] SetActiveTracks 호출됨 - 기존: [{string.Join(", ", activeTracks)}]");
        Debug.Log($"[DailyQuestManager] SetActiveTracks 호출됨 - 새로운: [{string.Join(", ", selectedTracks)}]");

        activeTracks.Clear();
        activeTracks.AddRange(selectedTracks);

        Debug.Log($"[DailyQuestManager] 활성 트랙 설정 완료: [{string.Join(", ", activeTracks)}]");
        Debug.Log($"[DailyQuestManager] 활성 트랙 개수: {activeTracks.Count}");
    }

    /// <summary>선택된 트랙으로 퀘스트 생성 (트랙 선택 UI에서 호출)</summary>
    public void GenerateQuestsForSelectedTracks()
    {
        if (activeTracks.Count == 0)
        {
            Debug.LogWarning("[DailyQuestManager] 활성 트랙이 없습니다!");
            return;
        }

        _todayStr = DateTime.Now.ToString("yyyy-MM-dd");
        GenerateForToday();
        Debug.Log($"[DailyQuestManager] 선택된 트랙으로 퀘스트 생성 완료: {activeTracks.Count}개");
    }

    // === 테스트용 메서드들 ===
    [ContextMenu("Test/Clear All Quests")]
    private void CM_ClearAllQuests()
    {
        Debug.Log("[DailyQuestManager] 퀘스트 초기화 시작");
        ClearAllQuests();
        Debug.Log("[DailyQuestManager] 퀘스트 초기화 완료");
        // RewardManager 초기화
        RewardManager.Instance.ResetToday();
    }

    [ContextMenu("Test/Delete Save Files")]
    private void CM_DeleteSaveFiles()
    {
        Debug.Log("[DailyQuestManager] 저장 파일 삭제 시작");
        try
        {
            string basePath = Path.Combine(Application.persistentDataPath, "DailyQuests");
            if (Directory.Exists(basePath))
            {
                var files = Directory.GetFiles(basePath, "*.json");
                foreach (var file in files)
                {
                    File.Delete(file);
                    Debug.Log($"[DailyQuestManager] 삭제된 파일: {Path.GetFileName(file)}");
                }

                Debug.Log($"[DailyQuestManager] 총 {files.Length}개 파일 삭제 완료");
            }

            // 메모리에서도 초기화
            _todayQuests.Clear();
            activeTracks.Clear();
            _save = new DailySave { date = _todayStr, quests = new QuestData[0], streak = 0 };
            OnQuestsGenerated?.Invoke(_todayQuests);

            Debug.Log("[DailyQuestManager] 저장 파일 삭제 및 초기화 완료");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DailyQuestManager] 저장 파일 삭제 중 오류: {ex.Message}");
        }
    }

    [ContextMenu("Test/Force Generate Quests")]
    private void CM_ForceGenerateQuests()
    {
        Debug.Log("[DailyQuestManager] 강제 퀘스트 생성 시작");
        _todayStr = DateTime.Now.ToString("yyyy-MM-dd");
        GenerateForToday();
        Debug.Log("[DailyQuestManager] 강제 퀘스트 생성 완료");
    }

    // ===== Internals =====
    private void LoadOrInit()
    {
        var path = PathFor(_todayStr);

        if (File.Exists(path))
        {
            _save = JsonUtility.FromJson<DailySave>(File.ReadAllText(path)) ?? new DailySave();
            _todayQuests.Clear();
            if (_save.quests != null)
            {
                _todayQuests.AddRange(_save.quests);
            }

            OnQuestsGenerated?.Invoke(_todayQuests);
        }
        else
        {
            // 저장 파일이 없으면 빈 상태로 유지 (트랙 선택 후 수동 생성)
            _todayQuests.Clear();
            _save = new DailySave
            {
                date = _todayStr,
                quests = new QuestData[0],
                streak = 0
            };
            OnQuestsGenerated?.Invoke(_todayQuests);
        }
    }

    private void GenerateForToday()
    {
        Debug.Log("[DailyQuestManager] GenerateForToday() 시작");

        // 이미 오늘 퀘스트가 생성되었는지 확인 (중복 생성 방지)
        var path = PathFor(_todayStr);
        if (File.Exists(path))
        {
            Debug.LogWarning($"[DailyQuestManager] 오늘({_todayStr}) 퀘스트가 이미 생성되어 있습니다. 중복 생성을 방지합니다.");
            // 기존 파일을 로드
            _save = JsonUtility.FromJson<DailySave>(File.ReadAllText(path)) ?? new DailySave();
            _todayQuests.Clear();
            if (_save.quests != null)
            {
                _todayQuests.AddRange(_save.quests);
                Debug.Log($"[DailyQuestManager] 기존 퀘스트 {_save.quests.Length}개를 로드했습니다.");
            }

            OnQuestsGenerated?.Invoke(_todayQuests);
            return;
        }

        int n = activeTracks.Count; // 제한 제거 - 인스펙터에서 설정한 만큼 생성
        Debug.Log($"[DailyQuestManager] 생성할 퀘스트 개수 (n): {n}");

        if (n <= 0)
        {
            Debug.LogWarning("[DailyQuestManager] activeTracks가 비어있습니다. 퀘스트를 생성할 수 없습니다.");
            return;
        }

        Debug.Log($"[DailyQuestManager] {n}개의 퀘스트를 생성합니다.");

        _todayQuests.Clear();
        Debug.Log("[DailyQuestManager] todayQuests 리스트 초기화 완료");

        int idx = 1;
        foreach (var t in activeTracks)
        {
            var questData = new QuestData
            {
                id = $"{_todayStr}-{t}-{idx++:000}",
                track = t,
                title = TitleOf(t),
                description = DescOf(t),
                status = QuestStatus.Pending
            };

            _todayQuests.Add(questData);
            Debug.Log($"[DailyQuestManager] 퀘스트 생성됨 - ID: {questData.id}, Track: {questData.track}, Title: {questData.title}");
        }

        Debug.Log($"[DailyQuestManager] 총 {_todayQuests.Count}개 퀘스트 생성 완료");

        _save = new DailySave
        {
            date = _todayStr,
            quests = _todayQuests.ToArray(),
            streak = _save?.streak ?? 0
        };

        Debug.Log($"[DailyQuestManager] DailySave 객체 생성 완료 - date: {_save.date}, quests 배열 길이: {_save.quests.Length}");

        SaveToday();
        Debug.Log("[DailyQuestManager] SaveToday() 호출 완료");

        // 이벤트 구독자 정보 확인
        if (OnQuestsGenerated != null)
        {
            var delegates = OnQuestsGenerated.GetInvocationList();
            Debug.Log($"[DailyQuestManager] OnQuestsGenerated 이벤트 구독자 수: {delegates.Length}");
            for (int i = 0; i < delegates.Length; i++)
            {
                Debug.Log($"[DailyQuestManager] 구독자[{i}]: {delegates[i].Target?.GetType().Name}.{delegates[i].Method.Name}");
            }

            OnQuestsGenerated.Invoke(_todayQuests);
            Debug.Log("[DailyQuestManager] OnQuestsGenerated 이벤트 발생 완료");
        }
        else
        {
            Debug.LogWarning("[DailyQuestManager] OnQuestsGenerated 이벤트에 구독자가 없습니다!");
        }
    }

    private void SaveToday()
    {
        Debug.Log("[DailyQuestManager] SaveToday() 시작");

        _save.quests = _todayQuests.ToArray();
        Debug.Log($"[DailyQuestManager] save.quests 배열 업데이트 - 길이: {_save.quests.Length}");

        string filePath = PathFor(_todayStr);
        Debug.Log($"[DailyQuestManager] 저장할 파일 경로: {filePath}");

        string jsonData = JsonUtility.ToJson(_save, true);
        Debug.Log($"[DailyQuestManager] JSON 데이터 생성 완료 - 길이: {jsonData.Length} characters");
        Debug.Log($"[DailyQuestManager] JSON 내용: {jsonData}");

        try
        {
            File.WriteAllText(filePath, jsonData);
            Debug.Log("[DailyQuestManager] 파일 저장 성공");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DailyQuestManager] 파일 저장 실패: {ex.Message}");
        }
    }

    private string PathFor(string date) => Path.Combine(BasePath, $"{date}.json");

    private void SetupNextGenTime()
    {
        var now = DateTime.Now;
        var today8 = new DateTime(now.Year, now.Month, now.Day, generateHour, 0, 0);
        _nextGenTimeLocal = (now >= today8)
            ? today8.AddDays(1)
            : today8;
    }

    private IEnumerator Scheduler()
    {
        while (true)
        {
            if (DateTime.Now >= _nextGenTimeLocal)
            {
                Debug.Log("[DailyQuestManager] Scheduler - 퀘스트 생성 시간이 되었습니다.");

                var newTodayStr = DateTime.Now.ToString("yyyy-MM-dd");
                Debug.Log($"[DailyQuestManager] Scheduler - 새로운 날짜: {newTodayStr}, 기존: {_todayStr}");

                // 날짜가 바뀐 경우 기존 퀘스트를 지우고 트랙 선택을 다시 할 수 있도록 설정
                if (newTodayStr != _todayStr)
                {
                    Debug.Log("[DailyQuestManager] Scheduler - 날짜가 바뀌었습니다. 새로운 트랙 선택이 필요합니다.");
                    _todayStr = newTodayStr;

                    // 새 날짜의 저장 파일이 있는지 확인
                    var newPath = PathFor(_todayStr);
                    if (!File.Exists(newPath))
                    {
                        Debug.Log("[DailyQuestManager] Scheduler - 새 날짜의 저장 파일이 없습니다. 트랙 선택을 기다립니다.");
                        // 자동 생성하지 않고 사용자의 트랙 선택을 기다림
                        ClearAllQuests(); // 기존 퀘스트를 클리어
                    }
                    else
                    {
                        Debug.Log("[DailyQuestManager] Scheduler - 새 날짜의 저장 파일이 이미 존재합니다. 로드합니다.");
                        LoadOrInit();
                    }
                }
                else
                {
                    Debug.Log("[DailyQuestManager] Scheduler - 같은 날짜이므로 퀘스트 생성을 건너뜁니다.");
                }

                SetupNextGenTime();
            }

            yield return new WaitForSeconds(5f);
        }
    }

    private string TitleOf(TrackType t) => t switch
    {
        TrackType.Portfolio => "오늘의 커밋/PR",
        TrackType.Knowledge => "지식 카드 1개",
        TrackType.JobHunt => "구직 진행 1회",
        TrackType.Free => "자유 퀘스트",
        _ => "퀘스트"
    };

    private string DescOf(TrackType t) => t switch
    {
        TrackType.Portfolio => "깃 커밋 또는 PR 1건",
        TrackType.Knowledge => "요약 카드 적기",
        TrackType.JobHunt => "이력서 수정/공고 기록 등",
        TrackType.Free => "자유롭게 목표 설정",
        _ => ""
    };

    // ===== GitHub 자동 검증 관련 메서드들 =====
    private IEnumerator AutoVerifyPortfolioQuests()
    {
        while (enableAutoVerification && _githubService != null)
        {
            yield return new WaitForSeconds(verificationInterval);

            // async Task를 코루틴에서 실행하기 위한 래퍼
            var verifyTask = VerifyPortfolioQuestsAsync();
            yield return new WaitUntil(() => verifyTask.IsCompleted);

            if (verifyTask.IsFaulted)
            {
                Debug.LogError($"[DailyQuestManager] Portfolio 퀘스트 자동 검증 중 오류: {verifyTask.Exception?.GetBaseException().Message}");
            }
        }
    }

    private async System.Threading.Tasks.Task VerifyPortfolioQuestsAsync()
    {
        Debug.Log("[DailyQuestManager] Portfolio 퀘스트 자동 검증 시작");

        // Portfolio 타입의 미완료 퀘스트들을 찾기
        var portfolioQuests = _todayQuests.FindAll(q =>
            q.track == TrackType.Portfolio &&
            q.status == QuestStatus.Pending
        );

        if (portfolioQuests.Count == 0)
        {
            Debug.Log("[DailyQuestManager] 검증할 Portfolio 퀘스트가 없습니다.");
            return;
        }

        Debug.Log($"[DailyQuestManager] {portfolioQuests.Count}개의 Portfolio 퀘스트 검증 중...");

        try
        {
            int commitCount = 0;

            // GitHub 설정 업데이트 시점이 설정되어 있는지 확인
            if (_githubSettingsUpdatedTime != DateTimeOffset.MinValue)
            {
                // 설정 업데이트 시점 이후의 커밋 수 조회
                commitCount = await _githubService.GetCommitCountSinceAsync(
                    githubOwner,
                    githubRepo,
                    githubOwner, // author로 사용 (빈 문자열 대신 owner 사용)
                    githubBranch, // 브랜치 지정 (빈 값이면 default branch 사용)
                    _githubSettingsUpdatedTime, // 설정 업데이트 시점 이후
                    CancellationToken.None
                );

                Debug.Log($"[DailyQuestManager] GitHub 설정 업데이트 시점 ({_githubSettingsUpdatedTime:yyyy-MM-dd HH:mm:ss}) 이후 커밋 수: {commitCount}");
            }
            else
            {
                // 설정 업데이트 시점이 없으면 오늘의 커밋으로 폴백
                commitCount = await _githubService.GetTodayCommitCountAsync(
                    githubOwner,
                    githubRepo,
                    githubOwner, // author로 사용 (빈 문자열 대신 owner 사용)
                    githubBranch, // 브랜치 지정 (빈 값이면 default branch 사용)
                    CancellationToken.None
                );

                Debug.Log($"[DailyQuestManager] GitHub 설정 시점이 없어 오늘의 커밋 수로 확인: {commitCount}");
            }

            // 커밋이 1개 이상이면 Portfolio 퀘스트들을 완료 처리
            if (commitCount > 0)
            {
                var timeDescription = _githubSettingsUpdatedTime != DateTimeOffset.MinValue 
                    ? $"GitHub 설정 업데이트 시점 ({_githubSettingsUpdatedTime:yyyy-MM-dd HH:mm:ss}) 이후"
                    : "오늘";

                Debug.Log($"🚀 [DailyQuestManager] {timeDescription}에 {commitCount}개의 커밋 확인! Portfolio 퀘스트 자동 완료 시작");

                foreach (var quest in portfolioQuests)
                {
                    Debug.Log($"🔧 [DailyQuestManager] GitHub 자동 검증으로 Portfolio 퀘스트 완료: {quest.id} - {quest.title}");
                    CompleteQuest(quest.id);
                }

                Debug.Log($"✨ [DailyQuestManager] GitHub 자동 검증 완료! {portfolioQuests.Count}개 Portfolio 퀘스트가 자동으로 완료되었습니다.");
            }
            else
            {
                var timeDescription = _githubSettingsUpdatedTime != DateTimeOffset.MinValue 
                    ? $"GitHub 설정 업데이트 시점 ({_githubSettingsUpdatedTime:yyyy-MM-dd HH:mm:ss}) 이후"
                    : "오늘";

                Debug.Log($"📝 [DailyQuestManager] {timeDescription}에 아직 커밋이 없습니다. Portfolio 퀘스트는 대기 상태로 유지됩니다.");
            }
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Debug.LogError($"[DailyQuestManager] GitHub API 네트워크 오류: {ex.Message}");
        }
        catch (System.Threading.Tasks.TaskCanceledException ex)
        {
            Debug.LogError($"[DailyQuestManager] GitHub API 요청 시간 초과: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DailyQuestManager] GitHub 커밋 확인 중 예상치 못한 오류: {ex.Message}");
            Debug.LogError($"[DailyQuestManager] 오류 타입: {ex.GetType().Name}");
            if (ex.InnerException != null)
            {
                Debug.LogError($"[DailyQuestManager] 내부 오류: {ex.InnerException.Message}");
            }
        }
    }


    // 다음 프레임에서 퀘스트 완료 이벤트 재발생 (안전성 강화)
    private IEnumerator InvokeQuestCompletedNextFrame(QuestData questData)
    {
        yield return null; // 다음 프레임 대기

        if (OnQuestCompleted != null)
        {
            OnQuestCompleted.Invoke(questData);
        }
    }
}