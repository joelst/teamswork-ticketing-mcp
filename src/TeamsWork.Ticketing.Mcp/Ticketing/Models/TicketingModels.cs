using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsWork.Ticketing.Mcp.Ticketing.Models;

/// <summary>A user reference (requestor, assignee, or acting user). For Microsoft 365 users use the Entra object ID.</summary>
public sealed record TicketUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email);

public sealed record TicketTag(
    [property: JsonPropertyName("tagCategoryId")] string TagCategoryId,
    [property: JsonPropertyName("text")] string Text);

public sealed record WorkflowTransition(
    [property: JsonPropertyName("targetId")] string? TargetId,
    [property: JsonPropertyName("transitionLabel")] string? TransitionLabel,
    [property: JsonPropertyName("authorizedUsers")] IReadOnlyList<string>? AuthorizedUsers,
    [property: JsonPropertyName("recordComment")] bool? RecordComment,
    [property: JsonPropertyName("recordResolutionSLA")] bool? RecordResolutionSla);

public sealed record WorkflowState(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("recordResolutionSLA")] bool? RecordResolutionSla,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<WorkflowTransition>? NextSteps);

/// <summary>
/// Ticket as returned by the API. Known fields are typed; anything the vendor adds later is preserved in
/// <see cref="Extra"/> so tool output never silently drops data.
/// </summary>
public sealed class Ticket
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("ticketNo")] public int? TicketNo { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("requestor")] public TicketUser? Requestor { get; init; }
    [JsonPropertyName("assignee")] public TicketUser? Assignee { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("description_HTML")] public string? DescriptionHtml { get; init; }
    [JsonPropertyName("priority")] public string? Priority { get; init; }
    [JsonPropertyName("expectedDate")] public string? ExpectedDate { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<TicketTag>? Tags { get; init; }
    [JsonPropertyName("customFields")] public JsonElement? CustomFields { get; init; }
    [JsonPropertyName("createdOn")] public string? CreatedOn { get; init; }
    [JsonPropertyName("lastUpdatedOn")] public string? LastUpdatedOn { get; init; }
    [JsonPropertyName("createdBy")] public TicketUser? CreatedBy { get; init; }
    [JsonPropertyName("lastUpdatedBy")] public TicketUser? LastUpdatedBy { get; init; }
    [JsonPropertyName("resolvedStatus")] public IReadOnlyList<string>? ResolvedStatus { get; init; }
    [JsonPropertyName("firstResponseOn")] public string? FirstResponseOn { get; init; }
    [JsonPropertyName("trackingId")] public string? TrackingId { get; init; }
    [JsonPropertyName("isCustomWorkflow")] public bool? IsCustomWorkflow { get; init; }
    [JsonPropertyName("workflow")] public IReadOnlyList<WorkflowState>? Workflow { get; init; }
    [JsonPropertyName("resolution")] public string? Resolution { get; init; }
    [JsonPropertyName("firstResolutionOn")] public string? FirstResolutionOn { get; init; }
    [JsonPropertyName("lastResolutionOn")] public string? LastResolutionOn { get; init; }
    [JsonPropertyName("lastResolutionComment")] public string? LastResolutionComment { get; init; }
    [JsonPropertyName("isFrtBreached")] public bool? IsFrtBreached { get; init; }
    [JsonPropertyName("isRtBreached")] public bool? IsRtBreached { get; init; }
    [JsonPropertyName("isFrtEscalated")] public bool? IsFrtEscalated { get; init; }
    [JsonPropertyName("isRtEscalated")] public bool? IsRtEscalated { get; init; }
    [JsonPropertyName("origin")] public string? Origin { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Writable subset of a ticket used for create and update. Null members are omitted from the request.</summary>
public sealed class TicketWrite
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("requestor")] public TicketUser? Requestor { get; init; }
    [JsonPropertyName("assignee")] public TicketUser? Assignee { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("description_HTML")] public string? DescriptionHtml { get; init; }
    [JsonPropertyName("priority")] public string? Priority { get; init; }
    [JsonPropertyName("expectedDate")] public string? ExpectedDate { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<TicketTag>? Tags { get; init; }
    [JsonPropertyName("customFields")] public JsonElement? CustomFields { get; init; }
}

public sealed record ActivityChange(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("oldValue")] JsonElement? OldValue,
    [property: JsonPropertyName("newValue")] JsonElement? NewValue);

public sealed class Attachment
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("caption")] public string? Caption { get; init; }
    [JsonPropertyName("createdDateTime")] public string? CreatedDateTime { get; init; }
    [JsonPropertyName("createdBy")] public string? CreatedBy { get; init; }
    [JsonPropertyName("src")] public string? Src { get; init; }
    [JsonPropertyName("isArchive")] public bool? IsArchive { get; init; }
    [JsonPropertyName("isHyperlink")] public bool? IsHyperlink { get; init; }
    [JsonPropertyName("isImage")] public bool? IsImage { get; init; }
    [JsonPropertyName("filename")] public string? Filename { get; init; }
}

public sealed class Activity
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("ticketId")] public string? TicketId { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("comment_HTML")] public string? CommentHtml { get; init; }
    [JsonPropertyName("isPrivate")] public bool? IsPrivate { get; init; }
    [JsonPropertyName("createdDateTime")] public string? CreatedDateTime { get; init; }
    [JsonPropertyName("createdBy")] public TicketUser? CreatedBy { get; init; }
    [JsonPropertyName("attachments")] public IReadOnlyList<Attachment>? Attachments { get; init; }
    [JsonPropertyName("changes")] public IReadOnlyList<ActivityChange>? Changes { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class CommentActivity
{
    [JsonPropertyName("activityId")] public string? ActivityId { get; init; }
    [JsonPropertyName("ticketId")] public string? TicketId { get; init; }
    [JsonPropertyName("ticketNo")] public int? TicketNo { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("comment_HTML")] public string? CommentHtml { get; init; }
    [JsonPropertyName("attachments")] public IReadOnlyList<Attachment>? Attachments { get; init; }
    [JsonPropertyName("user")] public TicketUser? User { get; init; }
}

public sealed record AttachmentLink(
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("caption")] string Caption);

public sealed class TagCategory
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("color")] public string? Color { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<Tag>? Tags { get; init; }
    [JsonPropertyName("deleted")] public bool? Deleted { get; init; }
}

public sealed record Tag(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("deleted")] bool? Deleted);

/// <summary>
/// Ticketing instance metadata. Only the parts an agent needs (custom fields, workflows, assignees, display info)
/// are typed; the rest is preserved in <see cref="Extra"/>.
/// </summary>
public sealed class Instance
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("instanceName")] public string? InstanceName { get; init; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("instanceType")] public string? InstanceType { get; init; }
    [JsonPropertyName("customFields")] public IReadOnlyList<CustomField>? CustomFields { get; init; }
    [JsonPropertyName("optionalFieldsLeft")] public IReadOnlyList<CustomField>? OptionalFieldsLeft { get; init; }
    [JsonPropertyName("optionalFieldsRight")] public IReadOnlyList<CustomField>? OptionalFieldsRight { get; init; }
    [JsonPropertyName("assignees")] public AssigneeConfig? Assignees { get; init; }
    [JsonPropertyName("workflows")] public JsonElement? Workflows { get; init; }
    [JsonPropertyName("sla")] public JsonElement? Sla { get; init; }
    [JsonPropertyName("isAutomaticAssignTickets")] public bool? IsAutomaticAssignTickets { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class CustomField
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("type")] public CustomFieldType? Type { get; init; }
    [JsonPropertyName("isMandatory")] public bool? IsMandatory { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("defaultValue")] public JsonElement? DefaultValue { get; init; }
    [JsonPropertyName("options")] public IReadOnlyList<CustomFieldOption>? Options { get; init; }
    [JsonPropertyName("isMultiple")] public bool? IsMultiple { get; init; }
}

public sealed record CustomFieldType(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("text")] string? Text);

public sealed record CustomFieldOption(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("text")] string? Text);

public sealed class AssigneeConfig
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("peoples")] public IReadOnlyList<Persona>? Peoples { get; init; }
}

public sealed record Persona(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("secondaryText")] string? SecondaryText);

// ---- Envelopes -------------------------------------------------------------------------------------------------

public sealed record ListResponse<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T>? Items,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken,
    [property: JsonPropertyName("itemCount")] int? ItemCount,
    [property: JsonPropertyName("error")] bool? Error,
    [property: JsonPropertyName("message")] string? Message);

public sealed record ItemResponse<T>(
    [property: JsonPropertyName("item")] T? Item,
    [property: JsonPropertyName("error")] bool? Error,
    [property: JsonPropertyName("message")] string? Message);

public sealed record ErrorResponse(
    [property: JsonPropertyName("error")] bool? Error,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("statusCode")] int? StatusCode);

// ---- Request bodies --------------------------------------------------------------------------------------------

public sealed record InsertTicketRequest(
    [property: JsonPropertyName("ticket")] TicketWrite Ticket,
    [property: JsonPropertyName("user")] TicketUser User);

public sealed record UpdateTicketRequest(
    [property: JsonPropertyName("ticket")] TicketWrite Ticket,
    [property: JsonPropertyName("user")] TicketUser User);

public sealed record UpdateTicketStatusRequest(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("resolution")] string? Resolution,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("user")] TicketUser User);

public sealed record InsertCommentRequest(
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("comment_HTML")] string? CommentHtml,
    [property: JsonPropertyName("isPrivate")] bool IsPrivate,
    [property: JsonPropertyName("user")] TicketUser User);

public sealed record InsertAttachmentLinkRequest(
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("comment_HTML")] string? CommentHtml,
    [property: JsonPropertyName("attachments")] IReadOnlyList<AttachmentLink> Attachments,
    [property: JsonPropertyName("isPrivate")] bool IsPrivate,
    [property: JsonPropertyName("user")] TicketUser User);

/// <summary>Filter, sort, and paging options for <c>GET /tickets</c>. Null values are not sent.</summary>
public sealed class TicketListQuery
{
    public string? Search { get; init; }
    public string? Title { get; init; }
    public string? Status { get; init; }
    public string? StatusId { get; init; }
    public bool? IsResolved { get; init; }
    public string? Priority { get; init; }
    public string? Tags { get; init; }
    public string? OrderBy { get; init; }
    public string? Order { get; init; }
    public string? Select { get; init; }
    public string? CreatedAfter { get; init; }
    public string? CreatedBefore { get; init; }
    public string? ExpectedDateAfter { get; init; }
    public string? ExpectedDateBefore { get; init; }
    public string? LastUpdateAfter { get; init; }
    public string? LastUpdateBefore { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public string? ContinuationToken { get; init; }
    public bool IncludeHtml { get; init; }
    public int? TimezoneOffset { get; init; }
}
