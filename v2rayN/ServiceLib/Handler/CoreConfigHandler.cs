namespace ServiceLib.Handler;

/// <summary>
/// Core configuration file processing class
/// </summary>
public static class CoreConfigHandler
{
    private static readonly string _tag = "CoreConfigHandler";

    public static async Task<RetResult> GenerateClientConfig(CoreConfigContext context, string? fileName)
    {
        var config = AppManager.Instance.Config;
        var result = new RetResult();
        var node = context.Node;

        if (node.ConfigType == EConfigType.Custom)
        {
            result = node.CoreType switch
            {
                ECoreType.mihomo => await new CoreConfigClashService(config, context.IsTunEnabled).GenerateClientCustomConfig(node, fileName),
                _ => await GenerateClientCustomConfig(node, fileName)
            };
        }
        else if (context.RunCoreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        }
        else
        {
            result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        }
        if (result.Success != true)
        {
            return result;
        }
        if (fileName.IsNotEmpty() && result.Data != null)
        {
            await File.WriteAllTextAsync(fileName, result.Data.ToString());
        }

        return result;
    }

    private static async Task<RetResult> GenerateClientCustomConfig(ProfileItem node, string? fileName)
    {
        var ret = new RetResult();
        try
        {
            if (node == null || fileName is null)
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }

            if (File.Exists(fileName))
            {
                File.SetAttributes(fileName, FileAttributes.Normal); //If the file has a read-only attribute, direct deletion will fail
                File.Delete(fileName);
            }

            var addressFileName = node.Address;
            if (!File.Exists(addressFileName))
            {
                addressFileName = Utils.GetConfigPath(addressFileName);
            }
            if (!File.Exists(addressFileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }
            File.Copy(addressFileName, fileName);
            File.SetAttributes(fileName, FileAttributes.Normal); //Copy will keep the attributes of addressFileName, so we need to add write permissions to fileName just in case of addressFileName is a read-only file.

            //check again
            if (!File.Exists(fileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            return await Task.FromResult(ret);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, string fileName, List<ServerTestItem> selecteds, ECoreType coreType)
    {
        var result = new RetResult();
        var dummyNode = new ProfileItem
        {
            CoreType = coreType
        };
        var builderResult = await CoreConfigContextBuilder.Build(config, dummyNode);
        var context = builderResult.Context;
        foreach (var testItem in selecteds)
        {
            var node = testItem.Profile;
            var (actNode, _) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node, true);
            if (node.IndexId == actNode.IndexId)
            {
                continue;
            }
            context.ServerTestItemMap[node.IndexId] = actNode.IndexId;
        }
        if (coreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(selecteds);
        }
        else if (coreType == ECoreType.Xray)
        {
            result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(selecteds);
        }
        if (result.Success != true)
        {
            return result;
        }
        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, CoreConfigContext context, ServerTestItem testItem, string fileName)
    {
        var result = new RetResult();
        var initPort = AppManager.Instance.GetLocalPort(EInboundProtocol.speedtest);
        var port = Utils.GetFreePort(initPort + testItem.QueueNum);
        testItem.Port = port;

        if (context.Node.ConfigType == EConfigType.Custom)
        {
            result = await GenerateCustomSpeedtestConfig(context.Node, port, fileName);
            if (result.Success == true)
            {
                testItem.AllowTest = true;
            }
            return result;
        }

        if (context.RunCoreType == ECoreType.sing_box)
        {
            result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(port);
        }
        else
        {
            result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(port);
        }
        if (result.Success != true)
        {
            return result;
        }

        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }

    // ponytail: byte-surgical inbound-port patch. Xray hard-fails the whole
    // config on strict outbound validation (e.g. fragment LengthMin), so the
    // user's file must reach the core byte-identical except the port digits.
    // Never re-serialize user JSON/YAML here.
    public static async Task<RetResult> GenerateCustomSpeedtestConfig(ProfileItem node, int port, string fileName)
    {
        var ret = new RetResult();
        try
        {
            var src = node.Address;
            if (!File.Exists(src))
            {
                src = Utils.GetConfigPath(src);
            }
            if (!File.Exists(src))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }
            var raw = await File.ReadAllBytesAsync(src);

            if (TryPatchJsonInboundPort(raw, port, out var patchedJson) && VerifyJsonOnlyInboundPortChanged(raw, patchedJson, port))
            {
                await File.WriteAllBytesAsync(fileName, patchedJson);
                ret.Success = true;
                ret.Msg = string.Format(ResUI.SuccessfulConfiguration, string.Empty);
                return ret;
            }

            if (TryPatchYamlMixedPort(raw, port, out var patchedYaml))
            {
                await File.WriteAllBytesAsync(fileName, patchedYaml);
                ret.Success = true;
                ret.Msg = string.Format(ResUI.SuccessfulConfiguration, string.Empty);
                return ret;
            }

            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    private static bool IsProxyInboundProto(string proto)
    {
        proto = proto.ToLowerInvariant();
        return proto.Contains("socks") || proto.Contains("http") || proto.Contains("mixed");
    }

    private static string JsonNodeToPlain(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }
        return node?.ToString() ?? string.Empty;
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out value))
            {
                return true;
            }
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out value))
            {
                return true;
            }
        }
        return false;
    }

    // Locate the single inbound port value to replace: first socks/http/mixed
    // inbound, else first inbound with a port. Exactly one inbound is patched
    // so co-located inbounds (dokodemo, tun, api) keep their ports.
    private static bool FindCustomInboundTarget(JsonArray inbounds, out int index, out string key)
    {
        index = -1;
        key = "port";
        for (var i = 0; i < inbounds.Count; i++)
        {
            if (inbounds[i] is not JsonObject o)
            {
                continue;
            }
            var proto = JsonNodeToPlain(o["protocol"] ?? o["type"]);
            if (!IsProxyInboundProto(proto))
            {
                continue;
            }
            if (o.ContainsKey("listen_port"))
            {
                index = i;
                key = "listen_port";
                return true;
            }
            if (o.ContainsKey("port"))
            {
                index = i;
                key = "port";
                return true;
            }
        }
        for (var i = 0; i < inbounds.Count; i++)
        {
            if (inbounds[i] is not JsonObject o)
            {
                continue;
            }
            if (o.ContainsKey("listen_port"))
            {
                index = i;
                key = "listen_port";
                return true;
            }
            if (o.ContainsKey("port"))
            {
                index = i;
                key = "port";
                return true;
            }
        }
        return false;
    }

    private static byte[] StripUtf8Bom(byte[] raw, out byte[] bom)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            bom = raw[..3];
            return raw[3..];
        }
        bom = [];
        return raw;
    }

    private static bool TryPatchJsonInboundPort(byte[] raw, int port, out byte[] patched)
    {
        patched = raw;
        var buf = StripUtf8Bom(raw, out var bom);

        JsonObject obj;
        JsonArray inbounds;
        try
        {
            var root = JsonNode.Parse(Encoding.UTF8.GetString(buf));
            if (root is not JsonObject o || o["inbounds"] is not JsonArray arr)
            {
                return false;
            }
            obj = o;
            inbounds = arr;
        }
        catch
        {
            return false;
        }
        if (!FindCustomInboundTarget(inbounds, out var targetIndex, out var key))
        {
            return false;
        }
        if (!TrySpliceInboundPortValue(buf, targetIndex, key, port, out var spliced))
        {
            return false;
        }
        patched = bom.Length > 0 ? [.. bom, .. spliced] : spliced;
        return true;
    }

    // Rewrite only the digits of inbounds[i].key via Utf8JsonReader token
    // spans. Direct child only: nested settings/fallback ports are ignored.
    private static bool TrySpliceInboundPortValue(byte[] buf, int targetIndex, string key, int port, out byte[] spliced)
    {
        spliced = buf;
        try
        {
            var reader = new Utf8JsonReader(buf, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var depth = 0;
            string? prop = null;
            var inInbounds = false;
            var elementIndex = -1;
            var inTarget = false;
            var targetDepth = -1;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (inInbounds && reader.TokenType == JsonTokenType.StartObject && depth == 2)
                        {
                            elementIndex++;
                            if (elementIndex == targetIndex)
                            {
                                inTarget = true;
                                targetDepth = depth + 1;
                            }
                        }
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        if (inTarget && depth < targetDepth)
                        {
                            inTarget = false;
                        }
                        if (depth == 1)
                        {
                            inInbounds = false;
                        }
                        break;
                    case JsonTokenType.PropertyName:
                        prop = reader.GetString();
                        if (depth == 1 && prop == "inbounds")
                        {
                            inInbounds = true;
                        }
                        break;
                    case JsonTokenType.Number:
                    case JsonTokenType.String:
                        if (inTarget && depth == targetDepth && prop == key)
                        {
                            var start = (int)reader.TokenStartIndex;
                            var end = (int)reader.BytesConsumed;
                            var quoted = reader.TokenType == JsonTokenType.String;
                            var digits = Encoding.ASCII.GetBytes(quoted ? $"\"{port}\"" : port.ToString());
                            spliced = [.. buf[..start], .. digits, .. buf[end..]];
                            return true;
                        }
                        break;
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    // Guarantee: patched file differs from source only in inbound ports.
    private static bool VerifyJsonOnlyInboundPortChanged(byte[] original, byte[] patched, int port)
    {
        try
        {
            var oldRoot = JsonNode.Parse(Encoding.UTF8.GetString(StripUtf8Bom(original, out _))) as JsonObject;
            var newRoot = JsonNode.Parse(Encoding.UTF8.GetString(StripUtf8Bom(patched, out _))) as JsonObject;
            if (oldRoot is null || newRoot is null)
            {
                return false;
            }
            if (oldRoot["inbounds"] is not JsonArray oldIn || newRoot["inbounds"] is not JsonArray newIn || oldIn.Count != newIn.Count)
            {
                return false;
            }
            var changed = 0;
            for (var i = 0; i < oldIn.Count; i++)
            {
                if (oldIn[i] is not JsonObject oo || newIn[i] is not JsonObject no)
                {
                    return false;
                }
                foreach (var k in new[] { "port", "listen_port" })
                {
                    var hadOld = oo.ContainsKey(k);
                    var hasNew = no.ContainsKey(k);
                    if (!hadOld && !hasNew)
                    {
                        continue;
                    }
                    if (!hadOld || !hasNew)
                    {
                        return false;
                    }
                    if (!TryGetInt(oo[k], out var ov) || !TryGetInt(no[k], out var nv))
                    {
                        return false;
                    }
                    if (ov != nv)
                    {
                        if (nv != port || k is not ("port" or "listen_port"))
                        {
                            return false;
                        }
                        changed++;
                        // restore for full-tree comparison below
                        no[k] = JsonNode.Parse(oo[k]!.ToJsonString());
                    }
                }
            }
            if (changed != 1)
            {
                return false;
            }
            var norm = new JsonSerializerOptions { WriteIndented = false };
            return oldRoot.ToJsonString(norm) == newRoot.ToJsonString(norm);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPatchYamlMixedPort(byte[] raw, int port, out byte[] patched)
    {
        patched = raw;
        string txt;
        try
        {
            txt = Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return false;
        }
        Dictionary<string, object>? yaml;
        try
        {
            yaml = YamlUtils.FromYaml<Dictionary<string, object>>(txt);
        }
        catch
        {
            return false;
        }
        if (yaml == null || (!yaml.ContainsKey("proxies") && !yaml.ContainsKey("mixed-port") && !yaml.ContainsKey("socks-port")))
        {
            return false;
        }
        var m = Regex.Match(txt, @"(?m)^(?<pre>\s*mixed-port\s*:\s*)(?<val>\d+)\s*$");
        if (!m.Success)
        {
            return false;
        }
        var val = m.Groups["val"];
        patched = Encoding.UTF8.GetBytes(txt[..val.Index] + port.ToString() + txt[(val.Index + val.Length)..]);
        try
        {
            var check = YamlUtils.FromYaml<Dictionary<string, object>>(Encoding.UTF8.GetString(patched));
            return check != null && check.TryGetValue("mixed-port", out var v) && v?.ToString() == port.ToString();
        }
        catch
        {
            return false;
        }
    }
}
