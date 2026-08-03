using Npgsql;
using PrismOne.Db.Core;

namespace PrismOne.Db.Cli;

/// <summary>
/// iapdb — PRISMONE DB 설치/패치 CLI.
/// psql 없이 Npgsql + PsqlScript(mini-psql)로 manifest.txt 를 실행한다.
/// macOS bash 3.2 에서 run_all.sh 가 돌지 않는 문제(mapfile 없음)와
/// Windows(psql 미설치) 배포가 이 CLI 의 존재 이유다.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["install", .. var rest] => await InstallAsync(ParseOptions(rest)),
                ["--help"] or ["-h"] or [] => Usage(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (OptionError ex)
        {
            Console.Error.WriteLine($"오류: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"실패: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            iapdb — PRISMONE DB 설치 도구 (psql 불필요)

            사용법:
              iapdb install [옵션]     manifest.txt 순서대로 초기 설치를 실행

            접속 (run_all.sh 와 같은 환경변수를 그대로 읽습니다):
              --host <h>          서버 호스트          [PGHOST, 기본 localhost]
              --port <p>          포트                 [PGPORT, 기본 5432]
              --super <u>         슈퍼유저             [PG_SUPER, 기본 postgres]
              --superpass <pw>    슈퍼유저 비밀번호     [PG_SUPERPASS, 없으면 프롬프트]

            설치 대상:
              --db-name <n>       만들 데이터베이스     [DB_NAME, 기본 prismone]
              --db-owner <r>      애플리케이션 역할     [DB_OWNER, 기본 prismone]
              --db-pass <pw>      역할 비밀번호         [DB_PASS, 기본 ***REMOVED***]
              --ts-data <path>    데이터 테이블스페이스 경로 [TS_DATA_PATH]
              --ts-idx <path>     인덱스 테이블스페이스 경로 [TS_IDX_PATH]

            기타:
              --root <dir>        repo 루트(manifest.txt 위치). 기본: 현재 위치에서 위로 탐색
              --manifest <file>   manifest 파일 경로 재지정
              --set-superpass <pw> 초기 비밀번호 설정값(무비밀번호 접속이 허용된 서버에서)
              --dry-run           접속·실행 없이 SQL 파싱과 실행 계획만 출력
              --verbose           실행하는 문장을 모두 출력

            postgres 계정에 비밀번호가 없는 초기 상태(trust 인증)로 접속되면
            먼저 초기 비밀번호를 설정한 뒤 설치를 진행합니다.
            """);
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"알 수 없는 명령: {command} — iapdb --help 참조");
        return 2;
    }

    // ---------- install ----------

    private static async Task<int> InstallAsync(Options options)
    {
        var root = options.Root ?? FindRoot()
            ?? throw new OptionError("manifest.txt 를 찾지 못했습니다 — repo 루트에서 실행하거나 --root 를 지정하세요");
        var manifestPath = options.Manifest ?? Path.Combine(root, "manifest.txt");
        if (!File.Exists(manifestPath))
            throw new OptionError($"manifest 가 없습니다: {manifestPath}");

        var steps = ParseManifest(manifestPath, root);
        Console.WriteLine($"=== PrismOne DB install ({steps.Count} steps, manifest: {manifestPath}) ===");

        // psql 변수 — run_all.sh build_psql() 의 -v 주입과 동일
        var variables = new Dictionary<string, string>
        {
            ["db_name"] = options.DbName,
            ["db_owner"] = options.DbOwner,
            ["db_pass"] = options.DbPass,
            ["ts_data"] = options.TsData,
            ["ts_idx"] = options.TsIdx,
        };

        if (options.DryRun)
            return DryRun(steps, variables);

        var superpass = await EnsureSuperuserAccessAsync(options);

        foreach (var (database, relPath, fullPath) in steps)
        {
            Console.WriteLine($"--- [{database}] {relPath}");
            // 파일마다 변수 사본 — 파일 안의 \set 이 다음 파일로 새지 않게 (psql -v 와 동일)
            var vars = new Dictionary<string, string>(variables);
            var units = PsqlScript.Parse(await File.ReadAllTextAsync(fullPath), vars);

            await using var conn = new NpgsqlConnection(BuildConnectionString(options, database, superpass));
            try
            {
                await conn.OpenAsync();
                var count = await PsqlScriptRunner.RunAsync(
                    conn, units, options.Verbose ? line => Console.WriteLine("    " + line) : null);
                Console.WriteLine($"    ok ({count} statements)");
            }
            catch (PostgresException ex)
            {
                Console.Error.WriteLine($"!! [{relPath}] {ex.MessageText}");
                if (ex.SqlState == "58P01" || ex.MessageText.Contains("tablespace", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine(
                        "   힌트: 테이블스페이스 디렉터리가 서버에 미리 만들어져 있어야 합니다\n" +
                        $"   (--ts-data {variables["ts_data"]} / --ts-idx {variables["ts_idx"]},\n" +
                        "   postgres OS 계정 소유의 빈 디렉터리 — install/linux/00_init_cluster.sh 참조)");
                return 1;
            }
        }

        Console.WriteLine("=== PrismOne DB install finished ===");
        return 0;
    }

    private static int DryRun(List<(string Db, string Rel, string Full)> steps, Dictionary<string, string> variables)
    {
        var total = 0;
        foreach (var (database, relPath, fullPath) in steps)
        {
            var vars = new Dictionary<string, string>(variables);
            var units = PsqlScript.Parse(File.ReadAllText(fullPath), vars);
            var gexec = units.Count(u => u is PsqlUnit.Gexec);
            Console.WriteLine($"--- [{database}] {relPath}: {units.Count} units" +
                (gexec > 0 ? $" (gexec {gexec})" : ""));
            total += units.Count;
        }
        Console.WriteLine($"=== dry-run ok — {total} units, 실행은 하지 않았습니다 ===");
        return 0;
    }

    /// <summary>
    /// 슈퍼유저 접속을 확보하고 실제로 쓸 비밀번호를 돌려준다.
    /// - 비밀번호 없이 접속되는 초기 상태(trust)면: 초기 비밀번호를 설정(ALTER ROLE)부터 진행.
    /// - 비밀번호가 틀리면: 대화형이면 다시 물어본다 (최대 3회).
    /// </summary>
    private static async Task<string> EnsureSuperuserAccessAsync(Options options)
    {
        var password = options.SuperPass;

        // 1) 우선 주어진 비밀번호(없으면 빈 값)로 시도
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(
                    BuildConnectionString(options, "postgres", password ?? ""));
                await conn.OpenAsync();

                if (string.IsNullOrEmpty(password))
                {
                    // 비밀번호 없이 열렸다 = trust 등 초기 상태. 초기 비밀번호 설정부터.
                    Console.WriteLine($"'{options.Super}' 계정이 비밀번호 없이 접속됩니다 — 초기 비밀번호를 설정합니다.");
                    var initial = options.SetSuperPass
                        ?? PromptPassword($"{options.Super} 의 초기 비밀번호 입력: ")
                        ?? throw new OptionError(
                            "초기 비밀번호가 필요합니다 — 대화형이 아니면 --set-superpass <pw> 를 지정하세요");
                    if (initial.Length == 0)
                        throw new OptionError("빈 비밀번호는 설정할 수 없습니다");

                    await using var alter = new NpgsqlCommand(
                        $"ALTER ROLE \"{options.Super.Replace("\"", "\"\"")}\" WITH PASSWORD " +
                        $"'{initial.Replace("'", "''")}'", conn);
                    await alter.ExecuteNonQueryAsync();
                    Console.WriteLine("초기 비밀번호를 설정했습니다. " +
                        "(주의: pg_hba.conf 가 여전히 trust 라면 scram-sha-256 으로 바꿔야 적용됩니다)");
                    return initial;
                }
                return password;
            }
            catch (Exception ex) when (IsAuthFailure(ex))
            {
                if (attempt >= 2)
                    throw new OptionError($"인증 실패({options.Super}@{options.Host}): {ex.Message}");
                var again = PromptPassword($"{options.Super} 비밀번호 입력: ");
                if (again is null)   // 비대화형 — 다시 물을 수 없다
                    throw new OptionError(string.IsNullOrEmpty(password)
                        ? $"비밀번호가 필요합니다 — --superpass 또는 PG_SUPERPASS 를 지정하세요 ({ex.Message})"
                        : $"인증 실패({options.Super}@{options.Host}) — 비밀번호를 확인하세요 ({ex.Message})");
                password = again;
            }
        }
    }

    private static bool IsAuthFailure(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState is "28P01" or "28000",
        NpgsqlException npg => npg.Message.Contains("password", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>콘솔이 대화형일 때만 비밀번호를 에코 없이 읽는다. 아니면 null.</summary>
    private static string? PromptPassword(string prompt)
    {
        if (Console.IsInputRedirected)
            return null;
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }

    private static string BuildConnectionString(Options options, string database, string password)
        => new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = database,
            Username = options.Super,
            Password = password,
            Pooling = false,
            Timeout = 15,
            CommandTimeout = 0,
        }.ConnectionString;

    // ---------- manifest / 옵션 ----------

    private static List<(string Db, string Rel, string Full)> ParseManifest(string manifestPath, string root)
    {
        var steps = new List<(string, string, string)>();
        foreach (var raw in File.ReadAllLines(manifestPath))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var bar = line.IndexOf('|');
            if (bar <= 0)
                throw new FormatException($"manifest 형식 오류 (db|path 여야 함): {line}");
            var database = line[..bar].Trim();
            var relPath = line[(bar + 1)..].Trim();
            var fullPath = Path.Combine(root, relPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"manifest 가 가리키는 파일이 없습니다: {fullPath}");
            steps.Add((database, relPath, fullPath));
        }
        return steps;
    }

    /// <summary>현재 위치에서 위로 올라가며 manifest.txt 가 있는 디렉터리를 찾는다.</summary>
    private static string? FindRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "manifest.txt")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private sealed class OptionError(string message) : Exception(message);

    private sealed record Options
    {
        public string Host { get; init; } = Env("PGHOST") ?? "localhost";
        public int Port { get; init; } = int.TryParse(Env("PGPORT"), out var p) ? p : 5432;
        public string Super { get; init; } = Env("PG_SUPER") ?? "postgres";
        public string? SuperPass { get; init; } = Env("PG_SUPERPASS");
        public string DbName { get; init; } = Env("DB_NAME") ?? "prismone";
        public string DbOwner { get; init; } = Env("DB_OWNER") ?? "prismone";
        public string DbPass { get; init; } = Env("DB_PASS") ?? "***REMOVED***";
        public string TsData { get; init; } = Env("TS_DATA_PATH") ?? "/data/pg_ts/prismone";
        public string TsIdx { get; init; } = Env("TS_IDX_PATH") ?? "/data/pg_ts/prismone_idx";
        public string? Root { get; init; }
        public string? Manifest { get; init; }
        public string? SetSuperPass { get; init; }
        public bool DryRun { get; init; }
        public bool Verbose { get; init; }

        private static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
    }

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new OptionError($"{args[i]} 뒤에 값이 필요합니다");
            options = args[i] switch
            {
                "--host" => options with { Host = Next() },
                "--port" => options with
                {
                    Port = int.TryParse(Next(), out var p) && p is > 0 and <= 65535
                        ? p : throw new OptionError("--port 는 1~65535 숫자여야 합니다"),
                },
                "--super" => options with { Super = Next() },
                "--superpass" => options with { SuperPass = Next() },
                "--db-name" => options with { DbName = Next() },
                "--db-owner" => options with { DbOwner = Next() },
                "--db-pass" => options with { DbPass = Next() },
                "--ts-data" => options with { TsData = Next() },
                "--ts-idx" => options with { TsIdx = Next() },
                "--root" => options with { Root = Path.GetFullPath(Next()) },
                "--manifest" => options with { Manifest = Path.GetFullPath(Next()) },
                "--set-superpass" => options with { SetSuperPass = Next() },
                "--dry-run" => options with { DryRun = true },
                "--verbose" => options with { Verbose = true },
                _ => throw new OptionError($"알 수 없는 옵션: {args[i]}"),
            };
        }
        return options;
    }
}
