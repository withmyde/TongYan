using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TongYan
{
    public static class AiClient
    {
        static AiClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)12288 | (SecurityProtocolType)3072 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls;
            }
            catch
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls;
            }
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.MaxServicePointIdleTime = 10000;
        }

        private static readonly object _reqLock = new object();
        private static HttpWebRequest _activeRequest = null;
        private static long _activeRequestId = 0;

        public static void AbortCurrent()
        {
            lock (_reqLock)
            {
                if (_activeRequest != null)
                {
                    try
                    {
                        _activeRequest.Abort();
                        Logger.Info("主动中止前序 HTTP 请求 ReqID=" + _activeRequestId);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("中止 HTTP 请求异常: " + ex.Message);
                    }
                    _activeRequest = null;
                }
            }
        }

        public static string BuildChatUrl(string baseUrl)
        {
            string u = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(u)) return "";

            if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return u;
            }
            if (u.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                return u.Substring(0, u.Length - "/models".Length) + "/chat/completions";
            }
            return u + "/chat/completions";
        }

        public static string BuildModelsUrl(string baseUrl)
        {
            string u = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(u)) return "";

            if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return u.Substring(0, u.Length - "/chat/completions".Length) + "/models";
            }
            if (u.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                return u;
            }
            return u + "/models";
        }

        public static void FetchModelsAsync(string baseUrl, string apiKey, Action<List<string>> onSuccess, Action<string> onError)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string url = BuildModelsUrl(baseUrl);
                if (string.IsNullOrEmpty(url))
                {
                    onError("请先填写接口地址");
                    return;
                }

                Logger.Info("开始拉取模型列表: " + url);

                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.Accept = "application/json";
                    request.UserAgent = "TongYan/1.0.0";
                    request.Timeout = 15000;
                    request.ReadWriteTimeout = 15000;

                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        request.Headers["Authorization"] = "Bearer " + apiKey.Trim();
                    }

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream resStream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(resStream, Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        JavaScriptSerializer serializer = new JavaScriptSerializer
                        {
                            MaxJsonLength = int.MaxValue
                        };

                        Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(body);
                        List<string> models = new List<string>();
                        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        if (root != null)
                        {
                            IEnumerable list = null;
                            if (root.ContainsKey("data"))
                            {
                                list = root["data"] as IEnumerable;
                            }
                            else if (root.ContainsKey("models"))
                            {
                                list = root["models"] as IEnumerable;
                            }

                            if (list != null)
                            {
                                foreach (object item in list)
                                {
                                    Dictionary<string, object> dict = item as Dictionary<string, object>;
                                    if (dict != null)
                                    {
                                        string id = null;
                                        if (dict.ContainsKey("id") && dict["id"] != null)
                                        {
                                            id = dict["id"].ToString().Trim();
                                        }
                                        else if (dict.ContainsKey("name") && dict["name"] != null)
                                        {
                                            id = dict["name"].ToString().Trim();
                                        }

                                        if (!string.IsNullOrEmpty(id) && !seen.Contains(id))
                                        {
                                            seen.Add(id);
                                            models.Add(id);
                                        }
                                    }
                                    else if (item is string)
                                    {
                                        string s = ((string)item).Trim();
                                        if (!string.IsNullOrEmpty(s) && !seen.Contains(s))
                                        {
                                            seen.Add(s);
                                            models.Add(s);
                                        }
                                    }
                                }
                            }
                        }

                        if (models.Count == 0)
                        {
                            Logger.Warn("模型列表接口返回为空: " + body);
                            onError("未获取到可用模型");
                            return;
                        }

                        Logger.Info("成功拉取模型数量: " + models.Count);
                        onSuccess(models);
                    }
                }
                catch (WebException wex)
                {
                    string errDetail = ExtractErrorDetail(wex);
                    string msg = "获取模型失败: " + (errDetail ?? wex.Message);
                    Logger.Error(msg, wex);
                    onError(msg);
                }
                catch (Exception ex)
                {
                    string msg = "获取模型出错: " + ex.Message;
                    Logger.Error(msg, ex);
                    onError(msg);
                }
            });
        }

        private static Dictionary<string, object> BuildPayload(AppConfig config, string text, bool applyDisableThinking)
        {
            string systemPrompt = config.GetActivePromptText();

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = config.model.Trim();
            payload["stream"] = true;
            payload["messages"] = new object[]
            {
                new Dictionary<string, object>
                {
                    { "role", "system" },
                    { "content", systemPrompt }
                },
                new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", text }
                }
            };

            if (applyDisableThinking)
            {
                string lowerUrl = (config.base_url ?? "").ToLowerInvariant();
                if (lowerUrl.Contains("aliyuncs.com") || lowerUrl.Contains("dashscope") || lowerUrl.Contains("siliconflow"))
                {
                    payload["enable_thinking"] = false;
                }
                else if (lowerUrl.Contains("volces.com") || lowerUrl.Contains("openai.com"))
                {
                    payload["reasoning_effort"] = "minimal";
                }
                else
                {
                    payload["thinking"] = new Dictionary<string, object>
                    {
                        { "type", "disabled" }
                    };
                }
            }

            return payload;
        }

        private static HttpWebRequest SendChatRequest(string url, string apiKey, Dictionary<string, object> payload, long requestId)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "text/event-stream, application/json";
            request.UserAgent = "TongYan/1.0.0";
            request.Timeout = 120000;
            request.ReadWriteTimeout = 60000;

            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers["Authorization"] = "Bearer " + apiKey.Trim();
            }

            lock (_reqLock)
            {
                if (_activeRequestId != requestId)
                {
                    try { request.Abort(); } catch { }
                    return null;
                }
                _activeRequest = request;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };
            string jsonBody = serializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);

            using (Stream reqStream = request.GetRequestStream())
            {
                reqStream.Write(bytes, 0, bytes.Length);
            }

            return request;
        }

        public static void StreamChatAsync(
            long requestId,
            string text,
            AppConfig config,
            Action onThinking,
            Action<string> onChunk,
            Action onComplete,
            Action<string> onError)
        {
            lock (_reqLock)
            {
                if (_activeRequest != null)
                {
                    try { _activeRequest.Abort(); } catch { }
                    _activeRequest = null;
                }
                _activeRequestId = requestId;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_reqLock)
                {
                    if (_activeRequestId != requestId)
                    {
                        Logger.Info("请求启动前已被新请求取代 ReqID=" + requestId);
                        return;
                    }
                }

                if (string.IsNullOrEmpty(config.base_url))
                {
                    Logger.Warn("未配置接口地址");
                    onError("未配置接口地址，请在设置中填写");
                    return;
                }

                if (string.IsNullOrEmpty(config.api_key))
                {
                    Logger.Warn("未配置 API Key");
                    onError("未配置 API Key，请在设置中填写");
                    return;
                }

                if (string.IsNullOrEmpty(config.model))
                {
                    Logger.Warn("未选择模型");
                    onError("未选择模型，请在设置中选择");
                    return;
                }

                string url = BuildChatUrl(config.base_url);
                Logger.Info(string.Format("开始流式请求 ReqID={0}, 模型={1}, 原文长度={2}", requestId, config.model, text.Length));

                HttpWebRequest request = null;
                HttpWebResponse response = null;

                try
                {
                    bool usedDisableThinking = config.disable_thinking;
                    Dictionary<string, object> payload = BuildPayload(config, text, usedDisableThinking);
                    request = SendChatRequest(url, config.api_key, payload, requestId);
                    if (request == null) return;

                    try
                    {
                        response = (HttpWebResponse)request.GetResponse();
                    }
                    catch (WebException firstEx)
                    {
                        HttpWebResponse errResp = firstEx.Response as HttpWebResponse;
                        bool isParamError = errResp != null &&
                            (errResp.StatusCode == HttpStatusCode.BadRequest || (int)errResp.StatusCode == 422);

                        if (usedDisableThinking && isParamError)
                        {
                            try { if (errResp != null) errResp.Close(); } catch { }
                            Logger.Warn("目标模型不支持关闭思考参数，自动切换标准请求重试 ReqID=" + requestId);

                            lock (_reqLock)
                            {
                                if (_activeRequestId != requestId) return;
                            }

                            Dictionary<string, object> fallbackPayload = BuildPayload(config, text, false);
                            request = SendChatRequest(url, config.api_key, fallbackPayload, requestId);
                            if (request == null) return;
                            response = (HttpWebResponse)request.GetResponse();
                        }
                        else
                        {
                            throw;
                        }
                    }

                    lock (_reqLock)
                    {
                        if (_activeRequestId != requestId)
                        {
                            try { response.Close(); } catch { }
                            try { request.Abort(); } catch { }
                            return;
                        }
                    }

                    JavaScriptSerializer serializer = new JavaScriptSerializer
                    {
                        MaxJsonLength = int.MaxValue
                    };

                    int chunkCount = 0;
                    bool notifiedThinking = false;

                    using (response)
                    using (Stream resStream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(resStream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lock (_reqLock)
                            {
                                if (_activeRequestId != requestId)
                                {
                                    try { request.Abort(); } catch { }
                                    return;
                                }
                            }

                            if (string.IsNullOrEmpty(line)) continue;
                            if (line.StartsWith("data:"))
                            {
                                string data = line.Substring(5).Trim();
                                if (data == "[DONE]")
                                {
                                    break;
                                }

                                try
                                {
                                    Dictionary<string, object> obj = serializer.Deserialize<Dictionary<string, object>>(data);
                                    if (obj != null)
                                    {
                                        if (obj.ContainsKey("error"))
                                        {
                                            Dictionary<string, object> errObj = obj["error"] as Dictionary<string, object>;
                                            string errMsg = errObj != null && errObj.ContainsKey("message") ? errObj["message"].ToString() : "接口返回错误";
                                            Logger.Error("接口返回业务错误: " + errMsg);
                                            onError("接口错误: " + errMsg);
                                            return;
                                        }

                                        if (obj.ContainsKey("choices"))
                                        {
                                            IEnumerable choices = obj["choices"] as IEnumerable;
                                            if (choices != null)
                                            {
                                                foreach (object item in choices)
                                                {
                                                    Dictionary<string, object> choice = item as Dictionary<string, object>;
                                                    if (choice != null && choice.ContainsKey("delta"))
                                                    {
                                                        Dictionary<string, object> delta = choice["delta"] as Dictionary<string, object>;
                                                        if (delta != null)
                                                        {
                                                            string chunk = delta.ContainsKey("content") ? delta["content"] as string : null;
                                                            if (!string.IsNullOrEmpty(chunk))
                                                            {
                                                                chunkCount++;
                                                                onChunk(chunk);
                                                            }
                                                            else if (chunkCount == 0 && !notifiedThinking && delta.ContainsKey("reasoning_content"))
                                                            {
                                                                string reasoning = delta["reasoning_content"] as string;
                                                                if (!string.IsNullOrEmpty(reasoning))
                                                                {
                                                                    notifiedThinking = true;
                                                                    if (onThinking != null)
                                                                    {
                                                                        onThinking();
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception parseEx)
                                {
                                    Logger.Warn("解析 SSE 数据包异常: " + parseEx.Message);
                                }
                            }
                        }
                    }

                    lock (_reqLock)
                    {
                        if (_activeRequestId != requestId) return;
                        if (_activeRequest == request) _activeRequest = null;
                    }

                    Logger.Info(string.Format("流式请求完成 ReqID={0}，共接收 {1} 个流式包", requestId, chunkCount));
                    if (chunkCount == 0)
                    {
                        onError("未收到有效内容");
                    }
                    else
                    {
                        onComplete();
                    }
                }
                catch (WebException wex)
                {
                    lock (_reqLock)
                    {
                        if (_activeRequestId != requestId)
                        {
                            Logger.Info("前序请求异常已被静默忽略 ReqID=" + requestId);
                            return;
                        }
                        if (_activeRequest == request) _activeRequest = null;
                    }

                    if (wex.Status == WebExceptionStatus.RequestCanceled)
                    {
                        Logger.Info("请求已被主动取消 ReqID=" + requestId);
                        return;
                    }

                    string errDetail = ExtractErrorDetail(wex);
                    string finalMsg = "请求失败: " + (errDetail ?? wex.Message);
                    Logger.Error(finalMsg, wex);
                    onError(finalMsg);
                }
                catch (Exception ex)
                {
                    bool wasCanceled = false;
                    lock (_reqLock)
                    {
                        if (_activeRequestId != requestId) return;
                        if (_activeRequest == null || _activeRequest != request) wasCanceled = true;
                        if (_activeRequest == request) _activeRequest = null;
                    }

                    if (wasCanceled || ex is ThreadAbortException)
                    {
                        Logger.Info("请求已被取消或中止 ReqID=" + requestId);
                        return;
                    }

                    Logger.Error("请求未知异常: " + ex.Message, ex);
                    onError("请求出错: " + ex.Message);
                }
            });
        }

        private static string ExtractErrorDetail(WebException wex)
        {
            string errDetail = null;
            try
            {
                if (wex.Response != null)
                {
                    using (StreamReader errReader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        string errBody = errReader.ReadToEnd();
                        Logger.Error("服务端原始错误响应: " + errBody);
                        try
                        {
                            JavaScriptSerializer ser = new JavaScriptSerializer
                            {
                                MaxJsonLength = int.MaxValue
                            };
                            Dictionary<string, object> errDict = ser.Deserialize<Dictionary<string, object>>(errBody);
                            if (errDict != null && errDict.ContainsKey("error"))
                            {
                                Dictionary<string, object> inner = errDict["error"] as Dictionary<string, object>;
                                if (inner != null && inner.ContainsKey("message") && inner["message"] != null)
                                {
                                    errDetail = inner["message"].ToString();
                                }
                                else if (errDict["error"] is string)
                                {
                                    errDetail = errDict["error"].ToString();
                                }
                            }
                            else if (errDict != null && errDict.ContainsKey("message") && errDict["message"] != null)
                            {
                                errDetail = errDict["message"].ToString();
                            }
                        }
                        catch { }

                        if (string.IsNullOrEmpty(errDetail))
                        {
                            errDetail = errBody;
                        }
                    }
                }
            }
            catch { }
            return errDetail;
        }
    }
}
