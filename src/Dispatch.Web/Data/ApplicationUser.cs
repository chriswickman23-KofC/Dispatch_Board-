using Microsoft.AspNetCore.Identity;

namespace Dispatch.Web.Data;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = "";
    public int? ZoneId { get; set; }   // dispatcher's default board filter
}
