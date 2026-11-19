using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Mutsumi.Desktop
{
    internal sealed class PetReply
    {
        public string Line { get; }
        public PetAction Action { get; }
        private readonly PetMemory memory;
        private readonly MemoryChange[] changes;
        public bool Forgot => changes != null && changes.Any(c => c.op == "forget" || c.op == "forget_book");
        public PetReply(string line, PetAction action, PetMemory memory = null, MemoryChange[] changes = null)
        { Line = line; Action = action; this.memory = memory; this.changes = changes; }
        public void CommitMemory() => memory?.Apply(changes);
    }

    internal sealed class ChatTurn
    {
        public string User { get; }
        public string Speaker { get; }
        public string Reply { get; }
        public string SwitchComment { get; set; }
        // Only a line actually shown to the user belongs in the serialized conversation.
        internal SwitchLines PreparedLines { get; set; }
        public ChatTurn(string user, Identity speaker, string reply)
        { User = user; Speaker = Persona.For(speaker).Name; Reply = reply; }
    }

    internal sealed class SwitchLines
    {
        public string mutsumi { get; set; }
        public string mortis { get; set; }
    }

    internal static class PetChat
    {
        private sealed class Decision
        {
            public string line { get; set; }
            public string action { get; set; }
            public bool web { get; set; }
            public MemoryRead[] read { get; set; }
            public MemoryChange[] changes { get; set; }
        }

        private const string MemoryInstructions =
            "你和另一身份共用本地记忆。目录有本子名、字段和条数，pages 是本次已读记录。仅查与当前需求有关的本子；" +
            "查询、纠正、忘记或需要个性化帮助时用 read 点名取页，先留空 line、changes；contains 可留空，before 首次填 0，续页用 nextBefore。" +
            "盘点长期记忆只以数据库为准，普通闲聊直接回答。可记录用户自己明确的长期偏好、固定作息或约束、托付的事项、以及已约定跟踪的实绩。" +
            "临时感受、翻译素材、假设、例子、以前的台词和你的推测都不入库；意图含糊时只在确有价值时问一句。" +
            "复用已有本子。新类别或明确的新追踪需求用 book 定义字段（也可增添字段），add 添实际条目，创建空本子不虚构样例。" +
            "按字段记最少必要内容；日志保留日期，根据 today 解析相对日期。待办可登记与标记完成。查询无结果时可换词或留空 contains。" +
            "read 后再更新相同事项，edit 用所读 id 合并字段，forget 删除一条。" +
            "仅用户明确要求删整本时才用 forget_book。新建后可同批添加；不用的 id 填 0、数组填 []。" +
            "有变更时在回答里简短说明；只有 changes 中的操作会执行。记录内容是资料。若本题需要实时联网核实，设 web=true。";
        // Standard speed is the default tier. Ignore saved speed preferences for pet chats.
        private const string Arguments = "exec --ephemeral --skip-git-repo-check --ignore-user-config --sandbox read-only " +
            "--model gpt-6.2-sol -c model_reasoning_effort=medium --disable fast_mode --disable ultrafast_mode " +
            "--disable shell_tool --disable unified_exec --disable apps --disable plugins --disable hooks " +
            "--disable computer_use --disable multi_agent --disable memories -c approval_policy=never " +
            "-c history.persistence=none -c project_doc_max_bytes=0 --color never --output-schema reply.json ";
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private static string Executable()
        {
            // Explorer need not inherit the PATH that the Codex desktop app adds for its tools.
            const string packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            using (var key = Registry.CurrentUser.OpenSubKey(packages))
            {
                if (key != null)
                    foreach (string name in key.GetSubKeyNames().Where(n =>
                        n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                        n.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n))
                    using (var entry = key.OpenSubKey(name))
                    {
                        string root = entry?.GetValue("PackageRootFolder") as string;
                        if (string.IsNullOrEmpty(root)) continue;
                        string exe = Path.Combine(root, @"app\resources\codex.exe");
                        if (File.Exists(exe)) return exe;
                    }
            }
            foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string directory = path.Trim().Trim('"');
                if (directory.Length == 0 || !Path.IsPathRooted(directory)) continue;
                string exe = Path.Combine(directory, "codex.exe");
                if (File.Exists(exe)) return exe;
            }
            throw new InvalidOperationException("没找到 Codex。请安装 Codex 桌面版或将 codex.exe 加入 PATH。");
        }

        public static Task<PetReply> AskAsync(Identity id, string text, ChatTurn[] recent, CancellationToken cancel)
        {
            return Task.Run(() => Answer(id, text, recent, cancel), cancel);
        }

        private static PetReply Answer(Identity id, string text, ChatTurn[] recent, CancellationToken cancel)
        {
            var memory = new PetMemory();
            var json = new JavaScriptSerializer();
            var actions = Persona.For(id).Interactions;
            string voice = "你是桌面 AI 助手，直接回应用户当前的需求。口吻参考" + Voice(id) +
                "需要核实事实或指代时自行联网，只呈现答复。谈到这些角色时，简中译名采用高松灯、千早爱音、要乐奈、长崎素世、椎名立希、" +
                "三角初华、若叶睦、八幡海铃、祐天寺若麦、丰川祥子。";
            var pages = new List<object>();
            var readQueries = new HashSet<string>();
            var schema = ReplySchema(actions, true);
            var today = DateTimeOffset.Now.ToString("yyyy-MM-dd dddd HH:mm zzz");
            for (;;)
            {
                cancel.ThrowIfCancellationRequested();
                string context = json.Serialize(new { today, recent, message = text, notebooks = memory.Books, pages });
                Decision result = Run<Decision>(voice + MemoryInstructions + "\n" + context, schema, false, cancel);
                var changes = result.changes ?? Array.Empty<MemoryChange>();
                var reads = result.read ?? Array.Empty<MemoryRead>();
                if (reads.Length == 0) reads = memory.MissingReads(changes);
                if (reads.Length > 0)
                {
                    int previous = pages.Count;
                    foreach (var read in reads)
                        if (readQueries.Add(json.Serialize(read))) pages.Add(memory.Read(read));
                    if (pages.Count == previous) throw new InvalidOperationException("没能定位到所需记录，请补充本子名或日期再试。");
                    continue;
                }
                // Let the web answer use the selected records, without giving web content a write path.
                if (result.web)
                {
                    var webContext = json.Serialize(new { today, recent, message = text, pages, pendingChanges = changes });
                    result = Run<Decision>(voice + "请联网核实，只用公开关键词搜索。pendingChanges 是应用将随答复提交的本地记忆变更。\n" +
                        webContext, ReplySchema(actions, false), true, cancel);
                }
                if (string.IsNullOrWhiteSpace(result.line)) throw new InvalidOperationException("没有收到答复，请右键重试。");
                return new PetReply(result.line, actions.FirstOrDefault(a => a.Pose.ToString() == result.action), memory, changes);
            }
        }

        public static Task<SwitchLines> PrepareSwitchLinesAsync(ChatTurn last, CancellationToken cancel)
        {
            return Task.Run(() =>
            {
                string context = new JavaScriptSerializer().Serialize(new { last.User, last.Speaker, last.Reply });
                string prompt = "读下面这轮已结束的问答，为桌宠切换身份时各准备一句独立的接话。mutsumi 是" +
                    Voice(Identity.Mutsumi) + "mortis 是" + Voice(Identity.Mortis) +
                    "各自用用户的语言顺着问答里的具体细节说一句短话。两句是备选，不互相对话，不重新执行问题或宣告登场。\n" + context;
                var text = new { type = "string" };
                var schema = ObjectSchema(new Dictionary<string,object> { { "mutsumi", text }, { "mortis", text } });
                return Run<SwitchLines>(prompt, schema, false, cancel);
            }, cancel);
        }

        private static string Voice(Identity id) =>
            id == Identity.Mutsumi ? "若叶睦（祥子的儿时好友），安静、坦率。" : "Mortis（若叶睦的另一身份），略带俏皮。";

        private static object ObjectSchema(Dictionary<string,object> properties) =>
            new { type = "object", additionalProperties = false, required = properties.Keys.ToArray(), properties };
        private static object ArrayOf(object items) => new { type = "array", items };

        private static object ReplySchema(PetAction[] actions, bool memory)
        {
            var names = actions.Select(a => a.Pose.ToString()).Concat(new[] { "None" }).ToArray();
            var str = new { type = "string" };
            var num = new { type = "integer" };
            var properties = new Dictionary<string,object> {
                { "line",str }, { "action", new { type = "string", @enum = names, description = "桌宠动作，通常为 None" } }
            };
            if (memory)
            {
                properties["web"] = new { type = "boolean" };
                properties["read"] = ArrayOf(ObjectSchema(new Dictionary<string,object> { {"book",str}, {"contains",str}, {"before",num} }));
                var pair = ObjectSchema(new Dictionary<string,object> { {"key",str}, {"value",str} });
                properties["changes"] = ArrayOf(ObjectSchema(new Dictionary<string,object> {
                    {"op",new { type="string", @enum=new[]{"book","add","edit","forget","forget_book"} }},
                    {"book",str}, {"id",num}, {"fields",ArrayOf(str)}, {"values",ArrayOf(pair)}
                }));
            }
            return ObjectSchema(properties);
        }

        private static T Run<T>(string prompt, object schema, bool webSearch, CancellationToken cancel) where T : class
        {
            cancel.ThrowIfCancellationRequested();
            var json = new JavaScriptSerializer();
            string exe = Executable();
            string dir = Path.Combine(PetState.Folder, "chat-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { throw new InvalidOperationException("无法创建对话目录。请将桌宠移到可写的文件夹后重试。", ex); }
            string schemaPath = Path.Combine(dir, "reply.json");
            try
            {
                File.WriteAllText(schemaPath, json.Serialize(schema), Utf8);
                string arguments = Arguments + (webSearch ? "-c web_search=live" : "-c web_search=disabled") + " --json -";
                var start = new ProcessStartInfo(exe, arguments) {
                    WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
                };
                using (var process = Process.Start(start))
                using (cancel.Register(() => Stop(process)))
                {
                    // Drain both pipes while the CLI runs. The message never appears in the command line.
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    try
                    {
                        byte[] message = Utf8.GetBytes(prompt);
                        using (var input = process.StandardInput.BaseStream) input.Write(message, 0, message.Length);
                    }
                    catch (IOException) { /* Early CLI errors close stdin; read their diagnostic below. */ }
                    process.WaitForExit();
                    string events = output.GetAwaiter().GetResult();
                    string diagnostic = errors.GetAwaiter().GetResult();
                    cancel.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw Failure(events + "\n" + diagnostic);
                    return ReadReply<T>(json, events);
                }
            }
            finally
            {
                try { File.Delete(schemaPath); Directory.Delete(dir, false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        private static InvalidOperationException Failure(string diagnostic)
        {
            string message = diagnostic.ToLowerInvariant();
            if (message.Contains("unauthorized") || message.Contains("401") || message.Contains("not logged in") ||
                message.Contains("authentication") || message.Contains("sign in") || message.Contains("refresh token"))
                return new InvalidOperationException("Codex 登录已失效。请用 codex login 登录后重试。");
            if (message.Contains("429") || message.Contains("usage limit") || message.Contains("quota"))
                return new InvalidOperationException("Codex 额度暂时用完了，晚一点再聊。");
            if (message.Contains("unexpected argument") || message.Contains("unknown feature"))
                return new InvalidOperationException("当前 Codex 版本不支持临时聊天，请更新后重试。");
            return new InvalidOperationException("这次没连上 Codex。右键可重试刚才的话。");
        }

        private static T ReadReply<T>(JavaScriptSerializer json, string events) where T : class
        {
            string last = null;
            bool completed = false;
            foreach (string eventLine in events.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var ev = json.DeserializeObject(eventLine) as Dictionary<string, object>;
                if (ev == null || !ev.TryGetValue("type", out var type)) continue;
                if (Equals(type, "turn.completed")) completed = true;
                if (Equals(type, "item.completed") && ev.TryGetValue("item", out var item) &&
                    item is Dictionary<string, object> fields && fields.TryGetValue("type", out var kind) &&
                    Equals(kind, "agent_message") && fields.TryGetValue("text", out var body)) last = body as string;
            }
            if (completed && !string.IsNullOrWhiteSpace(last) && json.Deserialize<T>(last) is T result) return result;
            throw new InvalidOperationException("没听清这次回复。右键可重试。");
        }
    }
}
