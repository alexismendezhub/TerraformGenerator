namespace TerraformGenerator.Domain;

// Application Gateway in Bicep, the same as in edge.tf.
public static partial class BicepGenerator
{
    private const string GatewaySubnet = "app-gateway";

    private static void Gateways(SketchModel model, BicepFile file)
    {
        foreach (var gateway in model.AppGateways)
        {
            Gateway(model, file, gateway);
        }
    }

    private static void Gateway(SketchModel model, BicepFile file, SketchResource gateway)
    {
        var symbol = Symbol(gateway);
        var origins = Kinds.Origins.SelectMany(kind => model.Targets(gateway, kind)).ToList();
        var https = TerraformGenerator.GatewayHttps(model, gateway);
        var vault = model.Targets(gateway, Kinds.KeyVault).FirstOrDefault();
        var firewall = Firewall.On(gateway);
        var listener = https ? "https" : "http";
        var certificate = gateway.P["certificate"];
        var main = origins.FirstOrDefault(origin => origin.P["path"] == "/*") ?? origins[0];
        string Child(string type, string name) => $"resourceId('Microsoft.Network/applicationGateways/{type}', {symbol}Name, '{name}')";
        BicepObject Reference(string type, string name) => Bicep.Object(reference => reference.Add("id", Child(type, name)));
        BicepObject Named(string name, Action<BicepObject> properties) => Bicep.Object(item => item.Str("name", name).Obj("properties", properties));

        file.Var($"{symbol}Name", Prefixed(gateway.Name));
        file.Resource(symbol + "Ip", "Microsoft.Network/publicIPAddresses@2024-05-01", Located(Prefixed($"{gateway.Name}-ip"))
            .Obj("sku", sku => sku.Str("name", "Standard"))
            .Obj("properties", properties => properties.Str("publicIPAllocationMethod", "Static")),
            $"Application Gateway {gateway.Name}: the public entry of {string.Join(", ", origins.Select(origin => origin.Name))}.");
        if (https && vault is not null)
        {
            file.Resource(symbol + "Identity", "Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31", Located(Prefixed($"{gateway.Name}-identity")),
                $"The gateway reads its certificate \"{certificate}\" from {vault.Name} as this identity.");
            Role(file, symbol + Symbol(vault) + "Role", Symbol(vault), "Key Vault Secrets User", $"{symbol}Identity.properties.principalId", $"{symbol}Identity.id");
        }

        if (firewall)
        {
            file.Resource(symbol + "Waf", "Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies@2024-05-01", Located(Prefixed($"{gateway.Name}-waf"))
                .Obj("properties", properties => properties
                    .Obj("policySettings", settings => settings
                        .Str("state", "Enabled")
                        .Str("mode", Firewall.Blocks(model, gateway) ? "Prevention" : "Detection")
                        .Add("requestBodyCheck", "true"))
                    .Obj("managedRules", managed => managed.Add("managedRuleSets", Bicep.Array(
                        Bicep.Object(set => set.Str("ruleSetType", "Microsoft_DefaultRuleSet").Str("ruleSetVersion", "2.1")),
                        Bicep.Object(set => set.Str("ruleSetType", "Microsoft_BotManagerRuleSet").Str("ruleSetVersion", "1.0")))))
                    .Add("customRules", Bicep.Array(Bicep.Object(rule => rule
                        .Str("name", "RequestLimit")
                        .Add("priority", "100")
                        .Str("ruleType", "RateLimitRule")
                        .Str("rateLimitDuration", "OneMin")
                        .Add("rateLimitThreshold", Firewall.Rate(gateway))
                        .Add("groupByUserSession", Bicep.Array(Bicep.Object(group => group
                            .Add("groupByVariables", Bicep.Array(Bicep.Object(variable => variable.Str("variableName", "ClientAddr")))))))
                        .Str("action", "Block")
                        .Add("matchConditions", Bicep.Array(Bicep.Object(condition => condition
                            .Add("matchVariables", Bicep.Array(Bicep.Object(variable => variable.Str("variableName", "RemoteAddr"))))
                            .Str("operator", "IPMatch")
                            .Add("negationConditon", "true")
                            .Add("matchValues", Bicep.Array("'127.0.0.1'"))))))))),
                $"The firewall of {gateway.Name}: " + (Firewall.Blocks(model, gateway) ? "blocks attacks" : "only logs attacks") + $" and limits each address to {Firewall.Rate(gateway)} requests a minute.");
        }

        var tier = firewall ? "WAF_v2" : "Standard_v2";
        var ports = new List<BicepValue> { Named("http", properties => properties.Add("port", "80")) };
        var listeners = new List<BicepValue>
        {
            Named("http", properties => properties
                .Add("frontendIPConfiguration", Reference("frontendIPConfigurations", "public"))
                .Add("frontendPort", Reference("frontendPorts", "http"))
                .Str("protocol", "Http")),
        };
        var rules = new List<BicepValue>();
        if (https)
        {
            ports.Add(Named("https", properties => properties.Add("port", "443")));
            listeners.Add(Named("https", properties => properties
                .Add("frontendIPConfiguration", Reference("frontendIPConfigurations", "public"))
                .Add("frontendPort", Reference("frontendPorts", "https"))
                .Str("protocol", "Https")
                .Add("sslCertificate", Reference("sslCertificates", certificate))));
            rules.Add(Named("http", properties => properties
                .Add("priority", "100")
                .Str("ruleType", "Basic")
                .Add("httpListener", Reference("httpListeners", "http"))
                .Add("redirectConfiguration", Reference("redirectConfigurations", "to-https"))));
        }

        rules.Add(Named("apps", properties =>
        {
            properties.Add("priority", "200").Add("httpListener", Reference("httpListeners", listener));
            if (origins.Count == 1)
            {
                properties.Str("ruleType", "Basic")
                    .Add("backendAddressPool", Reference("backendAddressPools", main.Name))
                    .Add("backendHttpSettings", Reference("backendHttpSettingsCollection", main.Name));
            }
            else
            {
                properties.Str("ruleType", "PathBasedRouting").Add("urlPathMap", Reference("urlPathMaps", "paths"));
            }
        }));

        file.Resource(symbol, "Microsoft.Network/applicationGateways@2024-05-01", Bicep.Object(body =>
        {
            body.Add("name", $"{symbol}Name").Add("location", Location).Add("tags", Tags);
            if (https)
            {
                body.Obj("identity", identity => identity.Str("type", "UserAssigned")
                    .Obj("userAssignedIdentities", ids => ids.Add($"'${{{symbol}Identity.id}}'", "{}")));
            }

            body.Obj("properties", properties =>
            {
                properties.Obj("sku", sku => sku.Str("name", tier).Str("tier", tier))
                    .Obj("autoscaleConfiguration", scale => scale.Add("minCapacity", gateway.P["minCapacity"]).Add("maxCapacity", gateway.P["maxCapacity"]));
                if (firewall)
                {
                    properties.Obj("firewallPolicy", policy => policy.Add("id", $"{symbol}Waf.id"));
                }

                properties.Add("gatewayIPConfigurations", Bicep.Array(Named("gateway", inner => inner.Obj("subnet", subnet => subnet.Add("id", SubnetId(model, GatewaySubnet))))))
                    .Add("frontendIPConfigurations", Bicep.Array(Named("public", inner => inner.Obj("publicIPAddress", ip => ip.Add("id", $"{symbol}Ip.id")))))
                    .Add("frontendPorts", Bicep.Array([.. ports]));
                if (https && vault is not null)
                {
                    properties.Add("sslCertificates", Bicep.Array(Named(certificate, inner => inner.Add("keyVaultSecretId", $"'${{{Symbol(vault)}.properties.vaultUri}}secrets/{certificate}'"))))
                        .Obj("sslPolicy", policy => policy.Str("policyType", "Predefined").Str("policyName", "AppGwSslPolicy20220101"))
                        .Add("redirectConfigurations", Bicep.Array(Named("to-https", inner => inner
                            .Str("redirectType", "Permanent")
                            .Add("targetListener", Reference("httpListeners", "https"))
                            .Add("includePath", "true")
                            .Add("includeQueryString", "true"))));
                }

                properties.Add("backendAddressPools", Bicep.Array(origins.Select(origin => (BicepValue)Named(origin.Name, inner => inner
                        .Add("backendAddresses", Bicep.Array(Bicep.Object(address => address.Add("fqdn", HostOf(origin)))))))
                        .ToArray()))
                    .Add("probes", Bicep.Array(origins.Select(origin => (BicepValue)Named(origin.Name, inner => inner
                        .Str("protocol", "Https")
                        .Str("path", "/")
                        .Add("interval", "30")
                        .Add("timeout", "30")
                        .Add("unhealthyThreshold", "3")
                        .Add("pickHostNameFromBackendHttpSettings", "true")
                        .Obj("match", match => match.Add("statusCodes", Bicep.Array("'200-399'"))))).ToArray()))
                    .Add("backendHttpSettingsCollection", Bicep.Array(origins.Select(origin => (BicepValue)Named(origin.Name, inner => inner
                        .Add("port", "443")
                        .Str("protocol", "Https")
                        .Str("cookieBasedAffinity", "Disabled")
                        .Add("requestTimeout", "30")
                        .Add("pickHostNameFromBackendAddress", "true")
                        .Add("probe", Reference("probes", origin.Name)))).ToArray()))
                    .Add("httpListeners", Bicep.Array([.. listeners]));
                if (origins.Count > 1)
                {
                    properties.Add("urlPathMaps", Bicep.Array(Named("paths", inner => inner
                        .Add("defaultBackendAddressPool", Reference("backendAddressPools", main.Name))
                        .Add("defaultBackendHttpSettings", Reference("backendHttpSettingsCollection", main.Name))
                        .Add("pathRules", Bicep.Array(origins.Where(origin => origin != main).Select(origin => (BicepValue)Named(origin.Name, rule => rule
                            .Add("paths", Bicep.Array(Bicep.Literal(origin.P["path"])))
                            .Add("backendAddressPool", Reference("backendAddressPools", origin.Name))
                            .Add("backendHttpSettings", Reference("backendHttpSettingsCollection", origin.Name)))).ToArray())))));
                }

                properties.Add("requestRoutingRules", Bicep.Array([.. rules]));
            });
            if (https && vault is not null)
            {
                body.Add("dependsOn", Bicep.Array(symbol + Symbol(vault) + "Role"));
            }
        }));

        foreach (var zone in model.Sources(gateway, Kinds.DnsZone))
        {
            file.Resource(symbol + Symbol(zone) + "Record", "Microsoft.Network/dnsZones/A@2018-05-01", Bicep.Object(body => body
                .Add("parent", Symbol(zone))
                .Add("name", Bicep.Literal(gateway.P["subdomain"]))
                .Obj("properties", properties => properties.Add("TTL", "300").Obj("targetResource", target => target.Add("id", $"{symbol}Ip.id")))),
                $"{gateway.P["subdomain"]}.{zone.P["zone"]} points to the gateway.");
        }
    }

    // Web and function apps behind a gateway take traffic only from its address.
    private static IEnumerable<BicepValue> GatewayRestrictions(SketchModel model, SketchResource app) =>
        model.Sources(app, Kinds.AppGateway).Select((gateway, index) => (BicepValue)Bicep.Object(rule => rule
            .Str("name", $"gateway-{gateway.Name}")
            .Add("priority", Number(200 + index))
            .Str("action", "Allow")
            .Add("ipAddress", $"'${{{Symbol(gateway)}Ip.properties.ipAddress}}/32'")));
}
