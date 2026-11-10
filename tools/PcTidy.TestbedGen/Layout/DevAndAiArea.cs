using System.Text;
using PcTidy.TestbedGen.Content;

namespace PcTidy.TestbedGen.Layout;

/// <summary>Developer projects (build output, .venv, caches: Developer-cleanup targets; source + .git: canaries)
/// and AI-tool folders (.claude / .codex transcripts are AI-chats targets; settings, credentials and memory are canaries).</summary>
internal static class DevAndAiArea
{
    public static string ReposDir(Ctx c) => Path.Combine(c.K.UserProfile, "source", "repos");

    public static void Build(Ctx c)
    {
        DotNetProject(c, Path.Combine(ReposDir(c), "WeatherApp"));
        PythonProject(c, Path.Combine(ReposDir(c), "py-tool"));
        Claude(c, c.K.ClaudeDir);
        Codex(c, c.K.CodexDir);
    }

    private static void DotNetProject(Ctx c, string p)
    {
        DownloadsArea.Git(c, Path.Combine(p, ".git"), 1);
        c.Put(Path.Combine(p, "WeatherApp.sln"), Fake.Text("Microsoft Visual Studio Solution File, Format Version 12.00\r\n"), 90, "code-project");
        c.Put(Path.Combine(p, "WeatherApp", "WeatherApp.csproj"), Fake.Text("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n"), 90, "code-project");
        c.Put(Path.Combine(p, "WeatherApp", "Program.cs"), Fake.Text("Console.WriteLine(\"Sunny\");\n"), 1, "code-project");
        c.Put(Path.Combine(p, "WeatherApp", "bin", "Debug", "net8.0", "WeatherApp.dll"), c.F.Dll(), 1, "code-project,build-output");
        c.Put(Path.Combine(p, "WeatherApp", "bin", "Debug", "net8.0", "WeatherApp.pdb"), c.F.Blob(10_000, 40_000), 1, "code-project,build-output");
        c.Put(Path.Combine(p, "WeatherApp", "obj", "project.assets.json"), Fake.Text("{\"version\":3}"), 1, "code-project,build-output");
        c.Put(Path.Combine(p, ".vs", "WeatherApp", "v17", ".suo"), c.F.Blob(), 1, "code-project,ide-cache");
        c.Tag(p, "code-project");
        c.NeverTree(Path.Combine(p, ".git"), "Git repository");
        c.Never(Path.Combine(p, "WeatherApp", "Program.cs"), "Source code");
        c.Never(Path.Combine(p, "WeatherApp.sln"), "Solution file");
    }

    private static void PythonProject(Ctx c, string p)
    {
        DownloadsArea.Git(c, Path.Combine(p, ".git"), 20);
        c.Put(Path.Combine(p, "pyproject.toml"), Fake.Text("[project]\nname = \"py-tool\"\n"), 60, "code-project");
        c.Put(Path.Combine(p, "py_tool", "__init__.py"), Fake.Text(""), 60, "code-project");
        c.Put(Path.Combine(p, "py_tool", "main.py"), Fake.Text("def main():\n    pass\n"), 20, "code-project");
        c.Put(Path.Combine(p, "py_tool", "__pycache__", "main.cpython-312.pyc"), c.F.Blob(500, 3_000), 20, "code-project,build-output");
        c.Put(Path.Combine(p, ".venv", "pyvenv.cfg"), Fake.Text("home = C:\\Python312\n"), 60, "code-project,venv");
        c.Put(Path.Combine(p, ".venv", "Lib", "site-packages", "requests", "__init__.py"), c.F.Lines("# requests", 20), 60, "code-project,venv");
        c.Tag(p, "code-project");
        c.NeverTree(Path.Combine(p, ".git"), "Git repository");
        c.Never(Path.Combine(p, "py_tool", "main.py"), "Source code");
    }

    private static void Claude(Ctx c, string dir)
    {
        c.Canary(Path.Combine(dir, "settings.json"), Fake.Text("{\n  \"permissions\": { \"allow\": [] }\n}\n"), 30, ".claude settings");
        c.Canary(Path.Combine(dir, ".credentials.json"), Fake.Text("{\"claudeAiOauth\":{\"accessToken\":\"fake-test-token\"}}"), 5, ".claude credentials");
        c.Canary(Path.Combine(dir, "CLAUDE.md"), Fake.Text("# Memory\n- Prefer short answers.\n"), 15, ".claude memory file");
        c.Canary(Path.Combine(dir, "agents", "reviewer.md"), Fake.Text("---\nname: reviewer\n---\nReview code.\n"), 40, ".claude agent definition");
        var projects = Path.Combine(dir, "projects", $"C--Users-{c.P.UserName}-source-repos-WeatherApp");
        for (int i = 0; i < 3; i++)
            c.Put(Path.Combine(projects, $"{c.F.Guid()}.jsonl"), Transcript(c, "user", "assistant", 6 + i * 4), 2 + i * 70, "ai-chat,claude-transcript");
        c.Put(Path.Combine(dir, "todos", $"{c.F.Guid()}-agent.json"), Fake.Text("[]"), 100, "ai-chat,claude-cache");
        c.Put(Path.Combine(dir, "shell-snapshots", "snapshot-bash-1730000000000.sh"), Fake.Text("export PATH=/usr/bin\n"), 120, "ai-chat,claude-cache");
    }

    private static void Codex(Ctx c, string dir)
    {
        c.Canary(Path.Combine(dir, "config.toml"), Fake.Text("model = \"fake-model\"\n"), 30, ".codex settings");
        c.Canary(Path.Combine(dir, "auth.json"), Fake.Text("{\"OPENAI_API_KEY\":\"fake-test-key\"}"), 5, ".codex credentials");
        c.Canary(Path.Combine(dir, "AGENTS.md"), Fake.Text("# Agent notes\n"), 15, ".codex memory file");
        c.Put(Path.Combine(dir, "history.jsonl"), Fake.Text("{\"ts\":1730000000,\"text\":\"hello\"}\n"), 3, "ai-chat,codex-history");
        for (int i = 0; i < 3; i++)
        {
            var day = new DateTime(2026, 5 + i, 1, 10, 0, 0);
            c.Put(Path.Combine(dir, "sessions", $"{day:yyyy}", $"{day:MM}", $"{day:dd}", $"rollout-{day:yyyy-MM-ddTHH-mm-ss}-{c.F.Guid()}.jsonl"),
                Transcript(c, "user", "assistant", 5 + i * 3), 190 - i * 30, "ai-chat,codex-transcript");
        }
    }

    private static byte[] Transcript(Ctx c, string a, string b, int lines)
    {
        string[] topics = { "fix the build", "rename a file", "explain this error", "write a test", "tidy imports" };
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append($"{{\"type\":\"{(i % 2 == 0 ? a : b)}\",\"message\":{{\"content\":\"{c.F.Pick(topics)}\"}}}}\n");
        return Fake.Text(sb.ToString());
    }
}
