namespace TerraformGenerator.Domain;

// keys.tf: the project's key in its own Key Vault, and an identity storage and SQL use to reach it.
public static partial class TerraformGenerator
{
    private const string EncryptionIdentity = "azurerm_user_assigned_identity.encryption";

    private static List<HclItem> Keys(SketchModel model)
    {
        var items = new List<HclItem>();
        if (!OwnKeys.Azure(model))
        {
            return items;
        }

        items.Add(new HclComment("The project's encryption key in a Key Vault of its own, rotated every year; storage and SQL reach it as this identity."));
        items.Add(Resource("azurerm_user_assigned_identity", "encryption")
            .Attr("name", Prefixed("encryption"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("tags", Tags));
        items.Add(Resource("azurerm_key_vault", "keys")
            .Attr("name", CompactName("keys"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("tenant_id", Tenant)
            .Str("sku_name", "standard")
            .Attr("rbac_authorization_enabled", "true")
            .Comment("Keys that encrypt data must never be lost: deleted keys are kept 90 days and cannot be purged.")
            .Attr("purge_protection_enabled", "true")
            .Attr("soft_delete_retention_days", "90")
            .Attr("tags", Tags));
        items.Add(RoleAssignment("keys_deployer", "azurerm_key_vault.keys.id", "Key Vault Crypto Officer", "data.azurerm_client_config.current.object_id", servicePrincipal: false));
        items.Add(RoleAssignment("keys_encryption", "azurerm_key_vault.keys.id", "Key Vault Crypto Service Encryption User", $"{EncryptionIdentity}.principal_id", servicePrincipal: true));
        items.Add(Resource("azurerm_key_vault_key", "data")
            .Str("name", "data")
            .Attr("key_vault_id", "azurerm_key_vault.keys.id")
            .Str("key_type", "RSA")
            .Attr("key_size", "2048")
            .Attr("key_opts", "[\"wrapKey\", \"unwrapKey\"]")
            .Block("rotation_policy", rotation => rotation
                .Str("expire_after", "P2Y")
                .Str("notify_before_expiry", "P29D")
                .Block("automatic", automatic => automatic.Str("time_after_creation", "P1Y")))
            .Attr("depends_on", "[azurerm_role_assignment.keys_deployer]"));
        return items;
    }

    // A storage account or the SQL server encrypted with the project's key.
    private static void StorageKey(SketchModel model, HclBlock account)
    {
        if (!OwnKeys.Azure(model))
        {
            return;
        }

        account.Block("identity", identity => identity.Str("type", "UserAssigned").Attr("identity_ids", $"[{EncryptionIdentity}.id]"))
            .Block("customer_managed_key", key => key
                .Attr("key_vault_key_id", "azurerm_key_vault_key.data.versionless_id")
                .Attr("user_assigned_identity_id", $"{EncryptionIdentity}.id"))
            .Attr("depends_on", "[azurerm_role_assignment.keys_encryption]");
    }

    private static void SqlKey(SketchModel model, HclBlock server)
    {
        if (!OwnKeys.Azure(model))
        {
            return;
        }

        server.Block("identity", identity => identity.Str("type", "UserAssigned").Attr("identity_ids", $"[{EncryptionIdentity}.id]"))
            .Attr("primary_user_assigned_identity_id", $"{EncryptionIdentity}.id")
            .Attr("transparent_data_encryption_key_vault_key_id", "azurerm_key_vault_key.data.id")
            .Attr("depends_on", "[azurerm_role_assignment.keys_encryption]");
    }
}
