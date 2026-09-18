// Gateway wire-contract samples used by tests. The same JSON is served by
// tests/fake_gateway.py (see sample_dashboard.json / sample_status.json).

namespace PersonalDashboard.Tests;

public static class GatewaySample
{
    public static readonly string DashboardJson =
        "{" +
        "\"generated_at\":\"2026-09-18T10:00:00Z\"," +
        "\"lessons\":[{" +
        "\"id\":\"l1\",\"method\":\"cache-rebuild\",\"scope\":\"project\",\"project_id\":\"lost-cyber-hamster-2025\"," +
        "\"title\":\"Пересборка кэша\",\"problem\":\"Спрайт не попадает в бандл\",\"conditions\":\"Android bundle\"," +
        "\"cause\":\"Кэш устарел\",\"working_method\":\"Пересобрать бандл\",\"evidence\":\"sha256:abc\"," +
        "\"verification\":\"хэш совпал\",\"status\":\"active\",\"agent\":\"codex\",\"computer\":\"DESKTOP\"," +
        "\"occurred_at\":\"2026-09-15T09:00:00Z\",\"applied_count\":4,\"verified_count\":2," +
        "\"last_read_at\":\"2026-09-15T09:00:00Z\",\"read_count\":2,\"last_applied_at\":\"2026-09-16T09:00:00Z\"}]," +
        "\"problems\":[{" +
        "\"id\":\"p1\",\"title\":\"Спрайт пропал\",\"summary\":\"Не попадает в бандл\",\"scope\":\"project\"," +
        "\"project_id\":\"lost-cyber-hamster-2025\",\"solution_count\":1,\"applied_count\":4,\"verified_count\":2," +
        "\"last_change\":\"2026-09-15T09:00:00Z\"}]," +
        "\"skills\":[{" +
        "\"id\":\"s1\",\"name\":\"graphify-use\",\"description\":\"Чтение графа\",\"scope\":\"global\",\"project_id\":\"*\"," +
        "\"status\":\"active\",\"source\":\"skills/graphify-use\",\"computer\":\"DESKTOP\",\"version\":\"v3\"," +
        "\"version_count\":3,\"dependencies\":[],\"verified_example\":\"...\"," +
        "\"registered_at\":\"2026-09-01T00:00:00Z\",\"updated_at\":\"2026-09-10T00:00:00Z\"}]," +
        "\"skill_events\":[{" +
        "\"id\":\"e1\",\"kind\":\"skill_used\",\"agent\":\"codex\",\"computer\":\"DESKTOP\"," +
        "\"occurred_at\":\"2026-09-15T09:00:00Z\",\"project_id\":\"lost-cyber-hamster-2025\",\"method\":\"x\"," +
        "\"skill\":\"graphify-use\",\"skill_id\":\"s1\",\"verification\":\"ok\",\"description\":\"\"}]," +
        "\"metrics\":{" +
        "\"lessons_total\":1,\"applied_total\":4,\"verified_total\":2,\"problems_total\":1,\"skills_total\":1," +
        "\"by_agent\":{\"codex\":{\"lessons\":1,\"applied\":4,\"verified\":2}}," +
        "\"by_project\":{\"lost-cyber-hamster-2025\":{\"lessons\":1,\"applied\":4,\"verified\":2}}," +
        "\"recent_prepares\":[{" +
        "\"agent\":\"codex\",\"computer\":\"DESKTOP\",\"occurred_at\":\"2026-09-15T09:00:00Z\"," +
        "\"project_id\":\"*\",\"task\":\"Починить спрайт\",\"found_records\":1,\"warning\":null}]," +
        "\"recent_reads\":[{\"id\":\"l1\",\"title\":\"Пересборка кэша\",\"read_at\":\"2026-09-15T09:00:00Z\"}]," +
        "\"recent_applied\":[{\"id\":\"l1\",\"title\":\"Пересборка кэша\",\"applied_at\":\"2026-09-16T09:00:00Z\"}]," +
        "\"recent_added\":[{\"id\":\"l1\",\"title\":\"Пересборка кэша\",\"added_at\":\"2026-09-15T09:00:00Z\"}]," +
        "\"note\":\"...\"}," +
        "\"window\":\"24h\"}";

    public static readonly string StatusJson =
        "{" +
        "\"database\":\"healthy\",\"hindsight\":\"healthy\"," +
        "\"worker\":{\"state\":\"idle\",\"last_error\":\"\"}," +
        "\"queue\":{\"queued\":1,\"processing\":0,\"completed\":42,\"failed\":0}," +
        "\"jobs\":[{\"event_id\":\"j1\",\"status\":\"done\"}]," +
        "\"computers\":[\"DESKTOP\"]}";
}