namespace ServiceLib.Tests.Handler;

public class CoreConfigHandlerCustomSpeedtestTests
{
    private static ProfileItem CustomNode(string address, ECoreType coreType = ECoreType.Xray)
    {
        return new ProfileItem
        {
            IndexId = Guid.NewGuid().ToString("N"),
            ConfigType = EConfigType.Custom,
            CoreType = coreType,
            Remarks = "custom-test",
            Address = address,
        };
    }

    private static async Task<string> WriteTemp(string content, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{ext}");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Test]
    public async Task XrayCustom_RewritesInboundPort()
    {
        var src = await WriteTemp("""
        {
          "inbounds": [{ "tag": "socks", "listen": "127.0.0.1", "port": 10808, "protocol": "socks" }],
          "outbounds": [{ "tag": "proxy", "protocol": "freedom" }]
        }
        """, ".json");
        var dst = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var ret = await CoreConfigHandler.GenerateCustomSpeedtestConfig(CustomNode(src), 18080, dst);
            await ret.Success.Should().BeTrue();
            var outJson = JsonUtils.ParseJson(await File.ReadAllTextAsync(dst)) as JsonObject;
            await outJson.Should().NotBeNull();
            await ((int)outJson!["inbounds"]!.AsArray()[0]!["port"]!).Should().BeEqualTo(18080);
        }
        finally
        {
            File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Test]
    public async Task SingboxCustom_RewritesListenPort()
    {
        var src = await WriteTemp("""
        {
          "inbounds": [{ "tag": "mixed-in", "type": "mixed", "listen": "127.0.0.1", "listen_port": 10808 }],
          "outbounds": [{ "tag": "proxy", "type": "direct" }]
        }
        """, ".json");
        var dst = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var ret = await CoreConfigHandler.GenerateCustomSpeedtestConfig(CustomNode(src, ECoreType.sing_box), 18081, dst);
            await ret.Success.Should().BeTrue();
            var outJson = JsonUtils.ParseJson(await File.ReadAllTextAsync(dst)) as JsonObject;
            await ((int)outJson!["inbounds"]!.AsArray()[0]!["listen_port"]!).Should().BeEqualTo(18081);
        }
        finally
        {
            File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Test]
    public async Task MihomoYaml_RewritesMixedPort()
    {
        var src = await WriteTemp("mixed-port: 7890\nproxies:\n  - name: a\n    type: ss\n", ".yaml");
        var dst = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var ret = await CoreConfigHandler.GenerateCustomSpeedtestConfig(CustomNode(src, ECoreType.mihomo), 18082, dst);
            await ret.Success.Should().BeTrue();
            var txt = await File.ReadAllTextAsync(dst);
            await txt.Should().Contain("18082");
        }
        finally
        {
            File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Test]
    public async Task MissingFile_Fails()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var ret = await CoreConfigHandler.GenerateCustomSpeedtestConfig(CustomNode("no-such-file.json"), 18083, dst);
            await ret.Success.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Test]
    public async Task JsonWithoutInbounds_Fails()
    {
        var src = await WriteTemp("""{ "outbounds": [] }""", ".json");
        var dst = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var ret = await CoreConfigHandler.GenerateCustomSpeedtestConfig(CustomNode(src), 18084, dst);
            await ret.Success.Should().BeFalse();
        }
        finally
        {
            File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Test]
    public async Task XrayFragmentOutbounds_StayByteIdentical()
    {
        // Regression: Xray rejects the whole config when a fragment mask has
        // LengthMin 0, so the patched file must equal the source everywhere
        // except the inbound port digits.
        var srcText = """
        {
          "log": { "loglevel": "warning" },
          "inbounds": [{ "tag": "socks", "listen": "127.0.0.1", "port": 10808, "protocol": "socks", "settings": { "auth": "noauth", "udp": true } }],
          "outbounds": [
            { "tag": "tcp-fragment", "protocol": "freedom", "streamSettings": { "sockopt": { "dialerProxy": "detour" }, "finalmask": { "tcp": [{ "type": "fragment", "settings": { "packets": "tlshello", "length": "100-200", "delay": "10-20", "maxSplit": 0 } }] } } },
            { "tag": "detour", "protocol": "vless", "settings": { "vnext": [{ "address": "example.com", "port": 443, "users": [{ "id": "00000000-0000-0000-0000-000000000001", "encryption": "none" }] }] } }
          ],
          "routing": { "domainStrategy": "AsIs", "rules": [] }
        }
        """;
        var src = await WriteTemp(srcText, ".json");
        var dst = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var ret = await CoreConfigHandler.GenerateCustomSpeedtestConfig(CustomNode(src), 18085, dst);
            await ret.Success.Should().BeTrue();
            var oldJson = JsonUtils.ParseJson(await File.ReadAllTextAsync(src)) as JsonObject;
            var newJson = JsonUtils.ParseJson(await File.ReadAllTextAsync(dst)) as JsonObject;
            await newJson.Should().NotBeNull();
            await ((int)newJson!["inbounds"]!.AsArray()[0]!["port"]!).Should().BeEqualTo(18085);
            var norm = new JsonSerializerOptions { WriteIndented = false };
            await newJson["outbounds"]!.ToJsonString(norm).Should().BeEqualTo(oldJson!["outbounds"]!.ToJsonString(norm));
            await newJson["routing"]!.ToJsonString(norm).Should().BeEqualTo(oldJson!["routing"]!.ToJsonString(norm));
        }
        finally
        {
            File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Test]
    public async Task MultiInbound_OnlyProxyInboundPatched()
    {
        var src = await WriteTemp("""
        {
          "inbounds": [
            { "tag": "dokodemo", "listen": "127.0.0.1", "port": 12345, "protocol": "dokodemo-door", "settings": { "address": "127.0.0.1", "port": 80, "network": "tcp" } },
            { "tag": "socks", "listen": "127.0.0.1", "port": 10808, "protocol": "socks" }
          ],
          "outbounds": [{ "tag": "proxy", "protocol": "freedom" }]
        }
        """, ".json");
        var dst = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var ret = await CoreConfigHandler.GenerateCustomSpeedtestConfig(CustomNode(src), 18086, dst);
            await ret.Success.Should().BeTrue();
            var newJson = JsonUtils.ParseJson(await File.ReadAllTextAsync(dst)) as JsonObject;
            var inbounds = newJson!["inbounds"]!.AsArray();
            await ((int)inbounds[0]!["port"]!).Should().BeEqualTo(12345);
            await ((int)inbounds[0]!["settings"]!["port"]!).Should().BeEqualTo(80);
            await ((int)inbounds[1]!["port"]!).Should().BeEqualTo(18086);
        }
        finally
        {
            File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }
}
