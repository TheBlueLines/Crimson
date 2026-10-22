using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Crimson.Models;

public class Game
{
	[JsonPropertyName("app_name")]
	public required string AppName { get; set; }

	[JsonPropertyName("app_title")]
	public required string AppTitle { get; set; }

	[JsonPropertyName("asset_infos")]
	public required AssetInfos AssetInfos { get; set; }

	[JsonPropertyName("base_urls")]
	public List<string>? BaseUrls { get; set; }

	[JsonPropertyName("metadata")]
	public required Metadata Metadata { get; set; }

	[JsonIgnore]
	public LocalAppState? LocalAppState { get; set; }

	public bool IsDlc()
	{
		return Metadata.MainGameItem != null;
	}
}

public class AdditionalCommandLine
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("value")]
	public string? Value { get; set; }
}

public class AgeGatings
{
	[JsonPropertyName("ESRB")]
	public ESRB? ESRB { get; set; }
}

public class AllowMultipleInstances
{
	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("value")]
	public string? Value { get; set; }
}

public class AppAccessType
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class AssetInfos
{
	[JsonPropertyName("Windows")]
	public required Asset Windows { get; set; }
}

public class AvailableDate
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class CanRunOffline
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class CanSkipCabinedWarning
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class CanSkipKoreanIdVerification
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class Category
{
	[JsonPropertyName("path")]
	public string Path { get; set; }
}

public class ComEpicgamesAppProductSlug
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class ComEpicgamesPortalProductContactSupportUrl
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class ComEpicgamesPortalProductPrivacyPolicyUrl
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class ComEpicgamesPortalProductWebsiteUrl
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class CustomAttributes
{
	[JsonPropertyName("CanRunOffline")]
	public CanRunOffline CanRunOffline { get; set; }

	[JsonPropertyName("CanSkipKoreanIdVerification")]
	public CanSkipKoreanIdVerification CanSkipKoreanIdVerification { get; set; }

	[JsonPropertyName("FolderName")]
	public FolderName FolderName { get; set; }

	[JsonPropertyName("MonitorPresence")]
	public MonitorPresence MonitorPresence { get; set; }

	[JsonPropertyName("OwnershipToken")]
	public OwnershipToken OwnershipToken { get; set; }

	[JsonPropertyName("PresenceId")]
	public PresenceId PresenceId { get; set; }

	[JsonPropertyName("RequirementsJson")]
	public RequirementsJson RequirementsJson { get; set; }

	[JsonPropertyName("UseAccessControl")]
	public UseAccessControl UseAccessControl { get; set; }

	[JsonPropertyName("NeverUpdate")]
	public NeverUpdate NeverUpdate { get; set; }

	[JsonPropertyName("partnerLinkId")]
	public PartnerLinkId PartnerLinkId { get; set; }

	[JsonPropertyName("partnerLinkType")]
	public PartnerLinkType PartnerLinkType { get; set; }

	[JsonPropertyName("availableDate")]
	public AvailableDate AvailableDate { get; set; }

	[JsonPropertyName("AllowMultipleInstances")]
	public AllowMultipleInstances AllowMultipleInstances { get; set; }

	[JsonPropertyName("com.epicgames.app.productSlug")]
	public ComEpicgamesAppProductSlug ComEpicgamesAppProductSlug { get; set; }

	[JsonPropertyName("AppAccessType")]
	public AppAccessType AppAccessType { get; set; }

	[JsonPropertyName("CanSkipCabinedWarning")]
	public CanSkipCabinedWarning CanSkipCabinedWarning { get; set; }

	[JsonPropertyName("HasGateKeeper")]
	public HasGateKeeper HasGateKeeper { get; set; }

	[JsonPropertyName("LaunchSocialOnFirstInstall")]
	public LaunchSocialOnFirstInstall LaunchSocialOnFirstInstall { get; set; }

	[JsonPropertyName("SysTrayRestore")]
	public SysTrayRestore SysTrayRestore { get; set; }

	[JsonPropertyName("com.epicgames.portal.product.contactSupportUrl")]
	public ComEpicgamesPortalProductContactSupportUrl ComEpicgamesPortalProductContactSupportUrl { get; set; }

	[JsonPropertyName("com.epicgames.portal.product.privacyPolicyUrl")]
	public ComEpicgamesPortalProductPrivacyPolicyUrl ComEpicgamesPortalProductPrivacyPolicyUrl { get; set; }

	[JsonPropertyName("com.epicgames.portal.product.websiteUrl")]
	public ComEpicgamesPortalProductWebsiteUrl ComEpicgamesPortalProductWebsiteUrl { get; set; }

	[JsonPropertyName("parentPartnerLinkId")]
	public ParentPartnerLinkId ParentPartnerLinkId { get; set; }

	[JsonPropertyName("AdditionalCommandLine")]
	public AdditionalCommandLine AdditionalCommandLine { get; set; }

	[JsonPropertyName("DlcProcessNames")]
	public DlcProcessNames DlcProcessNames { get; set; }
}

public class DlcItemList
{
	[JsonPropertyName("categories")]
	public List<Category> Categories { get; set; }

	[JsonPropertyName("creationDate")]
	public DateTime CreationDate { get; set; }

	[JsonPropertyName("customAttributes")]
	public CustomAttributes CustomAttributes { get; set; }

	[JsonPropertyName("description")]
	public string Description { get; set; }

	[JsonPropertyName("developer")]
	public string Developer { get; set; }

	[JsonPropertyName("developerId")]
	public string DeveloperId { get; set; }

	[JsonPropertyName("endOfSupport")]
	public bool EndOfSupport { get; set; }

	[JsonPropertyName("entitlementName")]
	public string EntitlementName { get; set; }

	[JsonPropertyName("entitlementType")]
	public string EntitlementType { get; set; }

	[JsonPropertyName("eulaIds")]
	public List<string> EulaIds { get; set; }

	[JsonPropertyName("id")]
	public string Id { get; set; }

	[JsonPropertyName("itemType")]
	public string ItemType { get; set; }

	[JsonPropertyName("keyImages")]
	public List<KeyImage> KeyImages { get; set; }

	[JsonPropertyName("lastModifiedDate")]
	public DateTime LastModifiedDate { get; set; }

	[JsonPropertyName("mainGameItem")]
	public MainGameItem MainGameItem { get; set; }

	[JsonPropertyName("namespace")]
	public string Namespace { get; set; }

	[JsonPropertyName("releaseInfo")]
	public List<ReleaseInfo> ReleaseInfo { get; set; }

	[JsonPropertyName("status")]
	public string Status { get; set; }

	[JsonPropertyName("title")]
	public string Title { get; set; }

	[JsonPropertyName("unsearchable")]
	public bool Unsearchable { get; set; }

	[JsonPropertyName("ageGatings")]
	public AgeGatings AgeGatings { get; set; }

	[JsonPropertyName("applicationId")]
	public string ApplicationId { get; set; }

	[JsonPropertyName("installModes")]
	public List<object> InstallModes { get; set; }

	[JsonPropertyName("requiresSecureAccount")]
	public bool? RequiresSecureAccount { get; set; }
}

public class DlcProcessNames
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class ESRB
{
	[JsonPropertyName("ageControl")]
	public int AgeControl { get; set; }

	[JsonPropertyName("descriptor")]
	public string Descriptor { get; set; }

	[JsonPropertyName("descriptorIds")]
	public List<int> DescriptorIds { get; set; }

	[JsonPropertyName("element")]
	public string Element { get; set; }

	[JsonPropertyName("elementIds")]
	public List<int> ElementIds { get; set; }

	[JsonPropertyName("gameRating")]
	public string GameRating { get; set; }

	[JsonPropertyName("isIARC")]
	public bool IsIARC { get; set; }

	[JsonPropertyName("isTrad")]
	public bool IsTrad { get; set; }

	[JsonPropertyName("ratingImage")]
	public string RatingImage { get; set; }

	[JsonPropertyName("ratingSystem")]
	public string RatingSystem { get; set; }

	[JsonPropertyName("title")]
	public string Title { get; set; }
}

public class FolderName
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class HasGateKeeper
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class KeyImage
{
	[JsonPropertyName("height")]
	public int Height { get; set; }

	[JsonPropertyName("md5")]
	public string Md5 { get; set; }

	[JsonPropertyName("size")]
	public int Size { get; set; }

	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("uploadedDate")]
	public DateTime UploadedDate { get; set; }

	[JsonPropertyName("url")]
	public string Url { get; set; }

	[JsonPropertyName("width")]
	public int Width { get; set; }
}

public class LaunchSocialOnFirstInstall
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class MainGameItem
{
	[JsonPropertyName("ageGatings")]
	public AgeGatings AgeGatings { get; set; }

	[JsonPropertyName("applicationId")]
	public string ApplicationId { get; set; }

	[JsonPropertyName("categories")]
	public List<Category> Categories { get; set; }

	[JsonPropertyName("creationDate")]
	public DateTime CreationDate { get; set; }

	[JsonPropertyName("customAttributes")]
	public CustomAttributes CustomAttributes { get; set; }

	[JsonPropertyName("description")]
	public string Description { get; set; }

	[JsonPropertyName("developer")]
	public string Developer { get; set; }

	[JsonPropertyName("developerId")]
	public string DeveloperId { get; set; }

	[JsonPropertyName("endOfSupport")]
	public bool EndOfSupport { get; set; }

	[JsonPropertyName("entitlementName")]
	public string EntitlementName { get; set; }

	[JsonPropertyName("entitlementType")]
	public string EntitlementType { get; set; }

	[JsonPropertyName("eulaIds")]
	public List<string> EulaIds { get; set; }

	[JsonPropertyName("id")]
	public string Id { get; set; }

	[JsonPropertyName("itemType")]
	public string ItemType { get; set; }

	[JsonPropertyName("keyImages")]
	public List<KeyImage> KeyImages { get; set; }

	[JsonPropertyName("lastModifiedDate")]
	public DateTime LastModifiedDate { get; set; }

	[JsonPropertyName("namespace")]
	public string Namespace { get; set; }

	[JsonPropertyName("releaseInfo")]
	public List<ReleaseInfo> ReleaseInfo { get; set; }

	[JsonPropertyName("status")]
	public string Status { get; set; }

	[JsonPropertyName("title")]
	public string Title { get; set; }

	[JsonPropertyName("unsearchable")]
	public bool Unsearchable { get; set; }
}

public class Metadata
{
	[JsonPropertyName("installationPoolId")]
	public string InstallationPoolId { get; set; }

	[JsonPropertyName("ageGatings")]
	public AgeGatings AgeGatings { get; set; }

	[JsonPropertyName("applicationId")]
	public string ApplicationId { get; set; }

	[JsonPropertyName("categories")]
	public List<Category> Categories { get; set; }

	[JsonPropertyName("creationDate")]
	public DateTime CreationDate { get; set; }

	[JsonPropertyName("customAttributes")]
	public CustomAttributes CustomAttributes { get; set; }

	[JsonPropertyName("description")]
	public string Description { get; set; }

	[JsonPropertyName("developer")]
	public string Developer { get; set; }

	[JsonPropertyName("developerId")]
	public string DeveloperId { get; set; }

	[JsonPropertyName("endOfSupport")]
	public bool EndOfSupport { get; set; }

	[JsonPropertyName("entitlementName")]
	public string EntitlementName { get; set; }

	[JsonPropertyName("entitlementType")]
	public string EntitlementType { get; set; }

	[JsonPropertyName("eulaIds")]
	public List<string> EulaIds { get; set; }

	[JsonPropertyName("id")]
	public string Id { get; set; }

	[JsonPropertyName("itemType")]
	public string ItemType { get; set; }

	[JsonPropertyName("keyImages")]
	public List<KeyImage> KeyImages { get; set; }

	[JsonPropertyName("lastModifiedDate")]
	public DateTime LastModifiedDate { get; set; }

	[JsonPropertyName("mainGameItem")]
	public MainGameItem MainGameItem { get; set; }

	[JsonPropertyName("namespace")]
	public string Namespace { get; set; }

	[JsonPropertyName("releaseInfo")]
	public List<ReleaseInfo> ReleaseInfo { get; set; }

	[JsonPropertyName("status")]
	public string Status { get; set; }

	[JsonPropertyName("title")]
	public string Title { get; set; }

	[JsonPropertyName("unsearchable")]
	public bool Unsearchable { get; set; }

	[JsonPropertyName("dlcItemList")]
	public List<DlcItemList> DlcItemList { get; set; }

	[JsonPropertyName("installModes")]
	public List<object> InstallModes { get; set; }

	[JsonPropertyName("longDescription")]
	public string LongDescription { get; set; }

	[JsonPropertyName("selfRefundable")]
	public bool? SelfRefundable { get; set; }

	[JsonPropertyName("entitlementStartDate")]
	public DateTime? EntitlementStartDate { get; set; }

	[JsonPropertyName("technicalDetails")]
	public string TechnicalDetails { get; set; }
}

public class MonitorPresence
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class NeverUpdate
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class OwnershipToken
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class ParentPartnerLinkId
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class PartnerLinkId
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class PartnerLinkType
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class PresenceId
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class ReleaseInfo
{
	[JsonPropertyName("appId")]
	public string AppId { get; set; }

	[JsonPropertyName("compatibleApps")]
	public List<object> CompatibleApps { get; set; }

	[JsonPropertyName("dateAdded")]
	public DateTime DateAdded { get; set; }

	[JsonPropertyName("id")]
	public string Id { get; set; }

	[JsonPropertyName("platform")]
	public List<string> Platform { get; set; }

	[JsonPropertyName("releaseNote")]
	public string ReleaseNote { get; set; }

	[JsonPropertyName("versionTitle")]
	public string VersionTitle { get; set; }
}

public class RequirementsJson
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class SysTrayRestore
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class UseAccessControl
{
	[JsonPropertyName("type")]
	public string Type { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class Asset
{
	[JsonPropertyName("appName")]
	public required string AppName { get; set; }

	[JsonPropertyName("assetId")]
	public string AssetId { get; set; }

	[JsonPropertyName("buildVersion")]
	public required string BuildVersion { get; set; }

	[JsonPropertyName("catalogItemId")]
	public required string CatalogItemId { get; set; }

	[JsonPropertyName("labelName")]
	public string LabelName { get; set; }

	[JsonPropertyName("metadata")]
	public object Metadata { get; set; }

	[JsonPropertyName("namespace")]
	public required string Namespace { get; set; }
}
