namespace MyApp.Api.Options;

public sealed class AccessPolicyOptions
{
    public string[] AllowedEmailDomains { get; set; } = [];
}
