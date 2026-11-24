namespace TerraformGenerator.Domain;

// The project's key in its own Key Vault, as in keys.tf.
public static partial class BicepGenerator
{
    private static void Keys(SketchModel model, BicepFile file)
    {
        if (!OwnKeys.Azure(model))
        {
            return;
        }

        file.Resource("encryptionIdentity", "Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31", Located(Prefixed("encryption")),
            "The project's encryption key in a Key Vault of its own, rotated every year; storage and SQL reach it as this identity.");
        file.Resource("keysVault", "Microsoft.KeyVault/vaults@2023-07-01", Located(CompactName("keys"))
            .Obj("properties", properties => properties
                .Add("tenantId", "tenant().tenantId")
                .Obj("sku", sku => sku.Str("family", "A").Str("name", "standard"))
                .Add("enableRbacAuthorization", "true")
                .Add("enableSoftDelete", "true")
                .Add("softDeleteRetentionInDays", "90")
                .Add("enablePurgeProtection", "true")));
        Role(file, "keysEncryption", "keysVault", "Key Vault Crypto Service Encryption User", "encryptionIdentity.properties.principalId", "encryptionIdentity.id");
        file.Resource("dataKey", "Microsoft.KeyVault/vaults/keys@2023-07-01", Bicep.Object(body => body
            .Add("parent", "keysVault")
            .Str("name", "data")
            .Obj("properties", properties => properties
                .Str("kty", "RSA")
                .Add("keySize", "2048")
                .Add("keyOps", Bicep.Array("'wrapKey'", "'unwrapKey'"))
                .Obj("rotationPolicy", rotation => rotation
                    .Obj("attributes", attributes => attributes.Str("expiryTime", "P2Y"))
                    .Add("lifetimeActions", Bicep.Array(
                        Bicep.Object(action => action.Obj("trigger", trigger => trigger.Str("timeAfterCreate", "P1Y")).Obj("action", type => type.Str("type", "rotate"))),
                        Bicep.Object(action => action.Obj("trigger", trigger => trigger.Str("timeBeforeExpiry", "P29D")).Obj("action", type => type.Str("type", "notify")))))))));
    }

    private static void StorageIdentity(SketchModel model, BicepObject account)
    {
        if (OwnKeys.Azure(model))
        {
            account.Obj("identity", identity => identity.Str("type", "UserAssigned")
                    .Obj("userAssignedIdentities", ids => ids.Add("'${encryptionIdentity.id}'", "{}")))
                .Add("dependsOn", Bicep.Array("keysEncryption"));
        }
    }

    private static void StorageEncryption(SketchModel model, BicepObject properties)
    {
        if (OwnKeys.Azure(model))
        {
            properties.Obj("encryption", encryption => encryption
                .Str("keySource", "Microsoft.Keyvault")
                .Obj("keyvaultproperties", key => key.Str("keyname", "data").Add("keyvaulturi", "keysVault.properties.vaultUri"))
                .Obj("identity", identity => identity.Add("userAssignedIdentity", "encryptionIdentity.id"))
                .Obj("services", services => services
                    .Obj("blob", blob => blob.Add("enabled", "true"))
                    .Obj("file", files => files.Add("enabled", "true")))
                .Add("requireInfrastructureEncryption", "false"));
        }
    }

    private static void SqlIdentity(SketchModel model, BicepObject server)
    {
        if (OwnKeys.Azure(model))
        {
            server.Obj("identity", identity => identity.Str("type", "UserAssigned")
                    .Obj("userAssignedIdentities", ids => ids.Add("'${encryptionIdentity.id}'", "{}")))
                .Add("dependsOn", Bicep.Array("keysEncryption"));
        }
    }

    private static void SqlEncryption(SketchModel model, BicepObject properties)
    {
        if (OwnKeys.Azure(model))
        {
            properties.Add("primaryUserAssignedIdentityId", "encryptionIdentity.id").Add("keyId", "dataKey.properties.keyUriWithVersion");
        }
    }
}
