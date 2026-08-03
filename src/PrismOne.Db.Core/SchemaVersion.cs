using Npgsql;

namespace PrismOne.Db.Core;

/// <summary>PRISMONE.schema_version 조회 결과 — 마지막 적용 패치와 적용 개수.</summary>
public sealed record SchemaVersionInfo(string? LatestVersionId, DateTime? AppliedAt, int AppliedCount)
{
    /// <summary>상태바용 짧은 라벨. 패치 기록이 없으면 "baseline".</summary>
    public string Label => LatestVersionId is null ? "baseline" : ShortLabel(LatestVersionId);

    /// <summary>"20260718_01_seed_encapsulated_sopclass.sql" → "20260718_01".
    /// 패턴이 다르면(자유 형식) .sql 만 떼고 그대로 둔다.</summary>
    public static string ShortLabel(string versionId)
    {
        var name = versionId.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            ? versionId[..^4]
            : versionId;
        var first = name.IndexOf('_');
        if (first >= 8 && name[..first].All(char.IsDigit))
        {
            var second = name.IndexOf('_', first + 1);
            if (second > first && name[(first + 1)..second].All(char.IsDigit))
                return name[..second];
        }
        return name;
    }
}

/// <summary>
/// 스키마 버전 조회 — Studio 상태바 표시용 (읽기 전용).
/// 설계 원칙: 패치 "적용"은 iapdb CLI(배포 키트)의 몫이고, Studio 는 서버가 아는
/// 상태(schema_version)를 보여주기만 한다 — Studio 가 스키마 버전에 종속되지 않게.
/// </summary>
public static class SchemaVersion
{
    /// <summary>PRISMONE DB 가 아니면(테이블 없음) null.</summary>
    public static async Task<SchemaVersionInfo?> LoadAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                select version_id, applied_dttm,
                       (select count(*) from prismone.schema_version)
                  from prismone.schema_version
                 order by applied_dttm desc, version_id desc
                 limit 1
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return new SchemaVersionInfo(
                    reader.GetString(0), reader.GetDateTime(1), reader.GetInt32(2));
            return new SchemaVersionInfo(null, null, 0);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "3F000" or "42501")
        {
            return null;   // 테이블/스키마 없음 또는 권한 없음 = PRISMONE 설치본 아님
        }
    }
}
