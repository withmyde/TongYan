using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace TongYan
{
    public class PromptPreset
    {
        public string id { get; set; }
        public string name { get; set; }
        public string prompt { get; set; }
    }

    public class AppConfig
    {
        public const string DefaultSystemPrompt =
            "你是一个专业的高精度翻译引擎。请将输入的文本翻译为自然流畅的简体中文。\n" +
            "【排版格式硬性要求】：\n" +
            "1. 必须严格保留原文的所有段落结构与换行！原文每一处换行、空行、列表符号、标题层级必须在译文中严格对应保留，绝对禁止将多行文字合并为一整段！\n" +
            "2. 仅输出翻译结果，绝不要输出任何解释、说明、备注或多余文字。\n" +
            "3. 若输入本身就是简体中文，则在完全保留原有排版格式的前提下润色为更加地道的简体中文。";

        public string base_url { get; set; }
        public string api_key { get; set; }
        public string model { get; set; }
        public bool disable_thinking { get; set; }
        public List<string> cached_models { get; set; }
        public string active_prompt_id { get; set; }
        public List<PromptPreset> prompts { get; set; }
        public string hotkey { get; set; }
        public bool auto_start { get; set; }

        public AppConfig()
        {
            base_url = "";
            api_key = "";
            model = "";
            disable_thinking = true;
            cached_models = new List<string>();
            active_prompt_id = "default";
            prompts = new List<PromptPreset>();
            hotkey = "Alt+D";
            auto_start = false;
        }

        public static List<PromptPreset> CreateDefaultPrompts()
        {
            return new List<PromptPreset>
            {
                new PromptPreset
                {
                    id = "default",
                    name = "默认翻译",
                    prompt = DefaultSystemPrompt
                }
            };
        }

        public void EnsureValid()
        {
            if (base_url == null) base_url = "";
            if (api_key == null) api_key = "";
            if (model == null) model = "";
            if (string.IsNullOrEmpty(hotkey)) hotkey = "Alt+D";

            if (cached_models == null)
            {
                cached_models = new List<string>();
            }

            if (prompts == null || prompts.Count == 0)
            {
                prompts = CreateDefaultPrompts();
            }

            bool foundActive = false;
            if (!string.IsNullOrEmpty(active_prompt_id))
            {
                for (int i = 0; i < prompts.Count; i++)
                {
                    if (prompts[i] != null && prompts[i].id == active_prompt_id)
                    {
                        foundActive = true;
                        break;
                    }
                }
            }

            if (!foundActive && prompts.Count > 0 && prompts[0] != null)
            {
                active_prompt_id = prompts[0].id;
            }
        }

        public PromptPreset GetActivePrompt()
        {
            EnsureValid();
            for (int i = 0; i < prompts.Count; i++)
            {
                if (prompts[i] != null && prompts[i].id == active_prompt_id)
                {
                    return prompts[i];
                }
            }
            return prompts[0];
        }

        public string GetActivePromptText()
        {
            PromptPreset preset = GetActivePrompt();
            if (preset != null && !string.IsNullOrEmpty(preset.prompt))
            {
                return preset.prompt;
            }
            return DefaultSystemPrompt;
        }

        public static string ConfigPath
        {
            get
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(dir, "config.json");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                string path = ConfigPath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                    json = json.TrimStart('\uFEFF', '\uFFFE', ' ', '\t', '\r', '\n');

                    JavaScriptSerializer serializer = new JavaScriptSerializer
                    {
                        MaxJsonLength = int.MaxValue
                    };
                    AppConfig cfg = serializer.Deserialize<AppConfig>(json);
                    if (cfg != null)
                    {
                        cfg.EnsureValid();
                        return cfg;
                    }
                }
            }
            catch { }

            AppConfig defaultCfg = new AppConfig();
            defaultCfg.EnsureValid();
            if (!File.Exists(ConfigPath))
            {
                defaultCfg.Save();
            }
            return defaultCfg;
        }

        public void Save()
        {
            try
            {
                EnsureValid();
                JavaScriptSerializer serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue
                };
                string json = serializer.Serialize(this);
                File.WriteAllText(ConfigPath, json, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
