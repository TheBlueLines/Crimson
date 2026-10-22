namespace Crimson.Models
{
	public class LoginResponse
	{
		public string redirectUrl { get; set; }
		public string authorizationCode { get; set; }
		public string? sid { get; set; }
	}
}
