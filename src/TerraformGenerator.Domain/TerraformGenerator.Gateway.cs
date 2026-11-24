namespace TerraformGenerator.Domain;

// Application Gateway: a public entry in the region with its own subnet, the Web Application Firewall, HTTPS with a
// certificate from Key Vault, and a route to each app by path.
public static partial class TerraformGenerator
{
    // The gateway's subnet: the third /24 of the network.
    private const string GatewayPrefix = "cidrsubnet(var.network_range, 8, 2)";

    private static List<SketchResource> GatewayOrigins(SketchModel model, SketchResource gateway) =>
        Kinds.Origins.SelectMany(kind => model.Targets(gateway, kind)).ToList();

    public static bool GatewayHttps(SketchModel model, SketchResource gateway) =>
        gateway.P["https"] == "keyVault" && model.Targets(gateway, Kinds.KeyVault).Any();

    private static IEnumerable<HclItem> Gateways(SketchModel model)
    {
        foreach (var gateway in model.AppGateways)
        {
            foreach (var item in Gateway(model, gateway))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<HclItem> Gateway(SketchModel model, SketchResource gateway)
    {
        var label = gateway.Label;
        var origins = GatewayOrigins(model, gateway);
        var https = GatewayHttps(model, gateway);
        var vault = model.Targets(gateway, Kinds.KeyVault).FirstOrDefault();
        var firewall = Firewall.On(gateway);
        var listener = https ? "https" : "http";
        yield return new HclComment($"Application Gateway {gateway.Name}: the public entry of {string.Join(", ", origins.Select(origin => origin.Name))}" +
            (firewall ? $", with its firewall ({(Firewall.Blocks(model, gateway) ? "blocking" : "only logging")})." : "."));
        yield return Resource("azurerm_public_ip", label)
            .Attr("name", Prefixed($"{gateway.Name}-ip"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Str("allocation_method", "Static")
            .Str("sku", "Standard")
            .Attr("tags", Tags);
        if (https && vault is not null)
        {
            yield return new HclComment($"The gateway reads its certificate \"{gateway.P["certificate"]}\" from {vault.Name} as this identity.");
            yield return Resource("azurerm_user_assigned_identity", $"{label}_gateway")
                .Attr("name", Prefixed($"{gateway.Name}-identity"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Attr("tags", Tags);
            yield return RoleAssignment($"{label}__{vault.Label}", $"azurerm_key_vault.{vault.Label}.id", "Key Vault Secrets User",
                $"azurerm_user_assigned_identity.{label}_gateway.principal_id", servicePrincipal: true);
        }

        if (firewall)
        {
            yield return Resource("azurerm_web_application_firewall_policy", label)
                .Attr("name", Prefixed($"{gateway.Name}-waf"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Attr("tags", Tags)
                .Block("policy_settings", settings => settings
                    .Attr("enabled", "true")
                    .Str("mode", Firewall.Blocks(model, gateway) ? "Prevention" : "Detection")
                    .Attr("request_body_check", "true"))
                .Block("managed_rules", rules => rules
                    .Block("managed_rule_set", set => set.Str("type", "Microsoft_DefaultRuleSet").Str("version", "2.1"))
                    .Block("managed_rule_set", set => set.Str("type", "Microsoft_BotManagerRuleSet").Str("version", "1.0")))
                .Block("custom_rules", rule => rule
                    .Str("name", "RequestLimit")
                    .Attr("priority", "100")
                    .Str("rule_type", "RateLimitRule")
                    .Str("rate_limit_duration", "OneMin")
                    .Attr("rate_limit_threshold", Firewall.Rate(gateway))
                    .Str("group_rate_limit_by", "ClientAddr")
                    .Str("action", "Block")
                    .Comment("Every address: the rule counts per address.")
                    .Block("match_conditions", condition => condition
                        .Block("match_variables", variable => variable.Str("variable_name", "RemoteAddr"))
                        .Str("operator", "IPMatch")
                        .Attr("negation_condition", "true")
                        .Attr("match_values", "[\"127.0.0.1\"]")));
        }

        var block = Resource("azurerm_application_gateway", label)
            .Attr("name", Prefixed(gateway.Name))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("tags", Tags);
        if (firewall)
        {
            block.Attr("firewall_policy_id", $"azurerm_web_application_firewall_policy.{label}.id");
        }

        var tier = firewall ? "WAF_v2" : "Standard_v2";
        block.Block("sku", sku => sku.Str("name", tier).Str("tier", tier))
            .Block("autoscale_configuration", scale => scale.Attr("min_capacity", gateway.P["minCapacity"]).Attr("max_capacity", gateway.P["maxCapacity"]));
        if (https)
        {
            block.Block("identity", identity => identity.Str("type", "UserAssigned").Attr("identity_ids", $"[azurerm_user_assigned_identity.{label}_gateway.id]"));
        }

        block.Block("gateway_ip_configuration", configuration => configuration.Str("name", "gateway").Attr("subnet_id", "azurerm_subnet.gateway.id"))
            .Block("frontend_ip_configuration", frontend => frontend.Str("name", "public").Attr("public_ip_address_id", $"azurerm_public_ip.{label}.id"))
            .Block("frontend_port", port => port.Str("name", "http").Attr("port", "80"));
        if (https && vault is not null)
        {
            block.Block("frontend_port", port => port.Str("name", "https").Attr("port", "443"))
                .Block("ssl_certificate", certificate => certificate
                    .Str("name", gateway.P["certificate"])
                    .Attr("key_vault_secret_id", Hcl.Str($"${{azurerm_key_vault.{vault.Label}.vault_uri}}secrets/{gateway.P["certificate"]}")))
                .Block("ssl_policy", policy => policy.Str("policy_type", "Predefined").Str("policy_name", "AppGwSslPolicy20220101"));
        }

        foreach (var origin in origins)
        {
            block.Block("backend_address_pool", pool => pool.Str("name", origin.Name).Attr("fqdns", Hcl.List([HostOf(origin)])))
                .Block("probe", probe => probe
                    .Str("name", origin.Name)
                    .Str("protocol", "Https")
                    .Str("path", "/")
                    .Attr("interval", "30")
                    .Attr("timeout", "30")
                    .Attr("unhealthy_threshold", "3")
                    .Attr("pick_host_name_from_backend_http_settings", "true")
                    .Block("match", match => match.Attr("status_code", "[\"200-399\"]")))
                .Block("backend_http_settings", settings => settings
                    .Str("name", origin.Name)
                    .Str("cookie_based_affinity", "Disabled")
                    .Attr("port", "443")
                    .Str("protocol", "Https")
                    .Attr("request_timeout", "30")
                    .Attr("pick_host_name_from_backend_address", "true")
                    .Str("probe_name", origin.Name));
        }

        block.Block("http_listener", http => http.Str("name", "http").Str("frontend_ip_configuration_name", "public").Str("frontend_port_name", "http").Str("protocol", "Http"));
        if (https)
        {
            block.Block("http_listener", secure => secure
                    .Str("name", "https")
                    .Str("frontend_ip_configuration_name", "public")
                    .Str("frontend_port_name", "https")
                    .Str("protocol", "Https")
                    .Str("ssl_certificate_name", gateway.P["certificate"]))
                .Block("redirect_configuration", redirect => redirect
                    .Str("name", "to-https")
                    .Str("redirect_type", "Permanent")
                    .Str("target_listener_name", "https")
                    .Attr("include_path", "true")
                    .Attr("include_query_string", "true"))
                .Block("request_routing_rule", rule => rule
                    .Str("name", "http")
                    .Attr("priority", "100")
                    .Str("rule_type", "Basic")
                    .Str("http_listener_name", "http")
                    .Str("redirect_configuration_name", "to-https"));
        }

        var main = origins.FirstOrDefault(origin => origin.P["path"] == "/*") ?? origins[0];
        if (origins.Count == 1)
        {
            block.Block("request_routing_rule", rule => rule
                .Str("name", "apps")
                .Attr("priority", "200")
                .Str("rule_type", "Basic")
                .Str("http_listener_name", listener)
                .Str("backend_address_pool_name", main.Name)
                .Str("backend_http_settings_name", main.Name));
        }
        else
        {
            block.Block("url_path_map", map =>
                {
                    map.Str("name", "paths").Str("default_backend_address_pool_name", main.Name).Str("default_backend_http_settings_name", main.Name);
                    foreach (var origin in origins.Where(origin => origin != main))
                    {
                        map.Block("path_rule", rule => rule
                            .Str("name", origin.Name)
                            .Attr("paths", Hcl.List([Hcl.Literal(origin.P["path"])]))
                            .Str("backend_address_pool_name", origin.Name)
                            .Str("backend_http_settings_name", origin.Name));
                    }
                })
                .Block("request_routing_rule", rule => rule
                    .Str("name", "apps")
                    .Attr("priority", "200")
                    .Str("rule_type", "PathBasedRouting")
                    .Str("http_listener_name", listener)
                    .Str("url_path_map_name", "paths"));
        }

        if (https && vault is not null)
        {
            block.Attr("depends_on", $"[azurerm_role_assignment.{label}__{vault.Label}]");
        }

        yield return block;
        foreach (var zone in model.Sources(gateway, Kinds.DnsZone))
        {
            yield return new HclComment($"{gateway.P["subdomain"]}.{zone.P["zone"]} points to the gateway.");
            yield return Resource("azurerm_dns_a_record", $"{label}__{zone.Label}")
                .Attr("name", Hcl.Literal(gateway.P["subdomain"]))
                .Attr("zone_name", $"azurerm_dns_zone.{zone.Label}.name")
                .Attr("resource_group_name", ResourceGroup)
                .Attr("ttl", "300")
                .Attr("target_resource_id", $"azurerm_public_ip.{label}.id")
                .Attr("tags", Tags);
        }
    }

    // Web and function apps behind a gateway take traffic only from its address.
    private static void GatewayRestrictions(HclBlock site, SketchModel model, SketchResource app)
    {
        var gateways = model.Sources(app, Kinds.AppGateway).ToList();
        for (var index = 0; index < gateways.Count; index++)
        {
            var gateway = gateways[index];
            site.Comment($"Only traffic from the Application Gateway {gateway.Name}.");
            site.Block("ip_restriction", rule => rule
                .Str("name", $"gateway-{gateway.Name}")
                .Attr("priority", Number(200 + index))
                .Str("action", "Allow")
                .Attr("ip_address", Hcl.Str(Interpolate($"azurerm_public_ip.{gateway.Label}.ip_address") + "/32")));
        }
    }
}
