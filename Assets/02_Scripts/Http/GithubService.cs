using System;
using System.Text.RegularExpressions;
using System.Threading;
using _02_Scripts.Http.Components;
using Newtonsoft.Json.Linq;
using UnityEngine;

// 0/1건 판별용

namespace _02_Scripts.Http // example.Services에서 Services로 변경
{
    public sealed class GithubService
    {
        private readonly GithubClient _client;

        public GithubService(GithubClient client) => _client = client;

        // 레포 메타 정보(존재, private, default_branch) 조회
        public async Awaitable<(bool exists, bool isPrivate, string defaultBranch)> GetRepositoryMetaAsync(
            string owner,
            string repo,
            CancellationToken ct)
        {
            var resp = await _client.GetRepositoryAsync(owner, repo, ct);
            if (resp.Ok && resp.Data != null)
                return (true, resp.Data.IsPrivate, resp.Data.DefaultBranch);

            // 200/301은 존재(리다이렉트 포함). 단, 본문 파싱 실패/권한 이슈로 세부정보는 기본값 사용.
            if (resp.Status == 200 || resp.Status == 301)
                return (true, false, null);

            // 404는 "미존재" 또는 "비공개 + 권한없음" 가능성. (보안상 404로 은닉) 
            return (false, false, null);
        }

        // 브랜치/커밋 지정 가능 버전
        public async Awaitable<int> GetTodayCommitCountAsync(
            string owner,
            string repo,
            string author,
            string branchOrSha,
            CancellationToken ct)
        {
            // UTC+9 (한국시간) 고정 오프셋으로 "오늘" 경계 계산
            var koreaOffset = TimeSpan.FromHours(9);
            var nowUtc = DateTimeOffset.UtcNow;
            var nowKorea = nowUtc.ToOffset(koreaOffset);
            
            // 한국시간 기준 오늘 00:00:00 ~ 23:59:59
            var startKorea = new DateTimeOffset(nowKorea.Year, nowKorea.Month, nowKorea.Day, 0, 0, 0, koreaOffset);
            var endKorea = startKorea.AddDays(1);
            
            // UTC로 변환
            var sinceUtc = startKorea.ToUniversalTime();
            var untilUtc = endKorea.ToUniversalTime();

            // per_page=1 → Link last page 번호 == 총 개수
            var resp = await _client.ListCommitsRawAsync(owner, repo, sinceUtc, untilUtc, author, perPage: 1, branchOrSha, ct);
            if (!resp.Ok)
            {
                Debug.LogWarning($"GitHub commits 실패: {resp.Status}\n{resp.Raw}");
                return 0;
            }

            if (resp.Headers.TryGetValue("Link", out var link))
            {
                var m = Regex.Match(link, @"[?&]page=(\d+)>;\s*rel=""last""");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                    return n;
            }

            try
            {
                var arr = JArray.Parse(resp.Raw);
                return arr.Count; // 0 or 1
            }
            catch
            {
                return 0;
            }
        }

        // 특정 시점(since) 이후의 커밋 수를 조회하는 메서드 추가
        public async Awaitable<int> GetCommitCountSinceAsync(
            string owner,
            string repo,
            string author,
            string branchOrSha,
            DateTimeOffset since,
            CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            
            // per_page=1 → Link last page 번호 == 총 개수
            var resp = await _client.ListCommitsRawAsync(owner, repo, since, now, author, perPage: 1, branchOrSha, ct);
            if (!resp.Ok)
            {
                Debug.LogWarning($"GitHub commits 실패: {resp.Status}\n{resp.Raw}");
                return 0;
            }

            if (resp.Headers.TryGetValue("Link", out var link))
            {
                var m = Regex.Match(link, @"[?&]page=(\d+)>;\s*rel=""last""");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                    return n;
            }

            try
            {
                var arr = JArray.Parse(resp.Raw);
                return arr.Count; // 0 or 1
            }
            catch
            {
                return 0;
            }
        }
    }
}