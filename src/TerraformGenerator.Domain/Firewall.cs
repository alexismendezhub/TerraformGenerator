using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// The web application firewall (WAF) in front of the apps: Front Door on Azure, CloudFront or the containers' load
// balancer on AWS, the HTTPS load balancer on Google Cloud. It filters the common attacks (SQL injection, scripts,
// known bad inputs, scanners) with the rules each cloud keeps up to date, and limits the requests of one address.
public static class Firewall
{
    public const string Key = "firewall";
    public const string RateKey = "rateLimit";

    public static PropertyDefinition Option(string help) =>
        new(Key, "Firewall (WAF)", PropertyType.Select, "auto",
        [
            O("auto", "On: blocks attacks in production, only logs them in other environments"), O("prevention", "On: blocks attacks"),
            O("detection", "Only logs attacks (to try it without blocking anyone)"), O("off", "Off"),
        ], null, help);

    public static PropertyDefinition Rate() =>
        new(RateKey, "Most requests per minute from one address", PropertyType.Select, "1000",
            [O("300", "300"), O("1000", "1,000"), O("2000", "2,000"), O("5000", "5,000")], null,
            "More than this from one address in a minute is refused for a while.", null, Key + "!=off");

    public static bool On(SketchResource entry) => entry.P[Key] != "off";

    public static bool Blocks(SketchModel model, SketchResource entry) =>
        entry.P[Key] == "prevention" || (entry.P[Key] == "auto" && model.IsProduction);

    public static string Rate(SketchResource entry) => entry.P[RateKey];

    // Entries with the firewall off in production, and public apps with nothing in front to filter their traffic.
    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        var entries = model.Resources.Where(resource => resource.P.ContainsKey(Key)).ToList();
        foreach (var entry in entries.Where(entry => !On(entry) && model.IsProduction))
        {
            yield return new(MessageLevel.Warning, $"{entry.Name}: production without its firewall (WAF); attacks like SQL injection reach the apps.", entry.Node.Id);
        }

        foreach (var entry in entries.Where(On).Where(entry => entry.P[Key] == "detection"))
        {
            yield return new(MessageLevel.Info, $"{entry.Name}: the firewall only logs attacks; switch it to block once the logs show no false alarms.", entry.Node.Id);
        }

        foreach (var frontDoor in model.FrontDoors.Where(On).Where(frontDoor => frontDoor.P["sku"] != "Premium_AzureFrontDoor"))
        {
            yield return new(MessageLevel.Info, $"{frontDoor.Name}: Standard gets the request limit; the attack rules Microsoft keeps need the Premium tier.", frontDoor.Node.Id);
        }

        foreach (var gateway in model.AppGateways)
        {
            if (!Kinds.Origins.Any(kind => model.Targets(gateway, kind).Any()))
            {
                yield return new(MessageLevel.Error, $"{gateway.Name}: draw an arrow from the gateway to the apps it serves.", gateway.Node.Id);
            }

            if (gateway.P["https"] == "keyVault" && !model.Targets(gateway, Kinds.KeyVault).Any())
            {
                yield return new(MessageLevel.Error, $"{gateway.Name}: HTTPS reads the certificate from a Key Vault; draw an arrow from the gateway to it.", gateway.Node.Id);
            }

            if (gateway.P["https"] == "keyVault" && model.Targets(gateway, Kinds.KeyVault).Any())
            {
                yield return new(MessageLevel.Info, $"{gateway.Name}: put the certificate \"{gateway.P["certificate"]}\" in the Key Vault before deploying; the gateway reads it when it starts.", gateway.Node.Id);
            }
            else if (model.IsProduction)
            {
                yield return new(MessageLevel.Warning, $"{gateway.Name}: production on HTTP only; choose HTTPS with a certificate in a Key Vault.", gateway.Node.Id);
            }

            foreach (var app in model.Targets(gateway, Kinds.ContainerApp).Concat(model.Targets(gateway, Kinds.StaticWebApp)))
            {
                yield return new(MessageLevel.Info, $"{app.Name}: it is still reachable directly too; only web and function apps are limited to the gateway.", app.Node.Id);
            }
        }

        if (!model.IsProduction)
        {
            yield break;
        }

        var open = model.Platform switch
        {
            Platforms.Azure => model.Resources.Where(app => (app.Kind is Kinds.WebApp or Kinds.FunctionApp or Kinds.StaticWebApp
                    || (app.Kind == Kinds.ContainerApp && app.Flag("external")))
                && !model.Sources(app, Kinds.FrontDoor).Any() && !model.Sources(app, Kinds.AppGateway).Any()),
            Platforms.Aws => model.OfKind(CloudKinds.Lambda).Where(function => function.Flag("publicUrl") && !model.Sources(function, CloudKinds.CloudFront).Any()),
            _ => model.Resources.Where(app => CloudKinds.GcpCompute.Contains(app.Kind) && app.Flag("public") && !model.Sources(app, CloudKinds.LoadBalancer).Any()),
        };
        var front = model.Platform switch
        {
            Platforms.Azure => "Front Door or Application Gateway",
            Platforms.Aws => "CloudFront",
            _ => "an HTTPS load balancer",
        };
        foreach (var app in open)
        {
            yield return new(MessageLevel.Warning, $"{app.Name}: it is public with no firewall in front. Put {front} in front of it and turn on its firewall.", app.Node.Id);
        }
    }
}
