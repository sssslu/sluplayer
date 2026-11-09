using System.IO;

namespace SluPlayer.Services.Downloads;

/// <summary>
/// A site with an in-app sign-in (<see cref="UI.Dialogs.LoginDialog"/>): where its cookies are saved, which cookies are
/// its own and what a signed-in session looks like. Each site has its own WebView2 profile, so signing out of one
/// leaves the other alone.
/// </summary>
public sealed class LoginSite
{
    public static readonly LoginSite YouTube = new(
        name: "YouTube",
        domain: "youtube.com",
        // YouTube's own sign-in link: Google login, then youtube.com/signin sets LOGIN_INFO (yt-dlp requires it to treat
        // the cookies as an authenticated session), then back to the home page.
        signInUrl: "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true" +
                   "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26hl%3Dko%26next%3Dhttps%253A%252F%252Fwww.youtube.com%252F" +
                   "&hl=ko",
        signInPrompt: "Google 계정으로 로그인",
        // The hosts yt-dlp talks to. The sign-in mirrors the account cookies (SID, SAPISID, __Secure-*PSID…) onto
        // .youtube.com, and yt-dlp only sends cookies of the host it requests, so the accounts.google.com / .google.com
        // cookies are never needed (and would open the whole Google account if leaked).
        cookieUris: ["https://www.youtube.com/", "https://youtube.com/", "https://m.youtube.com/", "https://music.youtube.com/"],
        // YouTube's page scripts rotate the session cookies, which would invalidate the copy handed to yt-dlp (see the
        // yt-dlp wiki on exporting YouTube cookies).
        parkUrl: "https://www.youtube.com/robots.txt",
        authCookies: ["SAPISID", "__Secure-3PAPISID", "__Secure-1PAPISID"],
        sessionCookies: ["__Secure-3PSID", "__Secure-1PSID", "SID", "LOGIN_INFO"],
        cookiesFile: AppPaths.YouTubeCookiesFile,
        webView2DataDir: AppPaths.WebView2DataDir,
        realPopups: false);

    public static readonly LoginSite X = new(
        name: "X",
        domain: "x.com",
        signInUrl: "https://x.com/i/flow/login",
        signInPrompt: "X 계정으로 로그인",
        cookieUris: ["https://x.com/"],
        parkUrl: "https://x.com/robots.txt",
        // auth_token is what yt-dlp checks for a signed-in session; ct0 is the CSRF token its API calls send back
        // (guests get one too, so it alone means nothing).
        authCookies: ["auth_token"],
        sessionCookies: ["ct0"],
        cookiesFile: AppPaths.XCookiesFile,
        webView2DataDir: AppPaths.XWebView2DataDir,
        // "Sign in with Google/Apple" report back to the page that opened their window.
        realPopups: true);

    public static IReadOnlyList<LoginSite> All { get; } = [YouTube, X];

    private readonly string _domain;
    private readonly string[] _authCookies;
    private readonly string[] _sessionCookies;

    private LoginSite(string name, string domain, string signInUrl, string signInPrompt, string[] cookieUris, string parkUrl,
        string[] authCookies, string[] sessionCookies, string cookiesFile, string webView2DataDir, bool realPopups)
    {
        Name = name;
        _domain = domain;
        SignInUrl = signInUrl;
        SignInPrompt = signInPrompt;
        CookieUris = cookieUris;
        ParkUrl = parkUrl;
        _authCookies = authCookies;
        _sessionCookies = sessionCookies;
        CookiesFile = cookiesFile;
        WebView2DataDir = webView2DataDir;
        RealPopups = realPopups;
    }

    /// <summary>Shown to the user ("YouTube 로그인", "X 계정").</summary>
    public string Name { get; }

    public string SignInUrl { get; }

    /// <summary>Status line of the sign-in window while it waits for the user.</summary>
    public string SignInPrompt { get; }

    /// <summary>Where the session cookies are read from (the hosts yt-dlp requests).</summary>
    public IReadOnlyList<string> CookieUris { get; }

    /// <summary>A page without scripts, opened before the cookies are read so the site's pages can't change them.</summary>
    public string ParkUrl { get; }

    /// <summary>The exported Netscape cookies.txt; it exists while signed in.</summary>
    public string CookiesFile { get; }

    public string WebView2DataDir { get; }

    /// <summary>Pop-ups open as real windows (sign-in providers that talk to their opener) instead of in place.</summary>
    public bool RealPopups { get; }

    public bool IsLoggedIn => File.Exists(CookiesFile);

    public bool IsSiteHost(string host)
    {
        host = host.Trim().TrimStart('.').ToLowerInvariant();
        return host == _domain || host.EndsWith("." + _domain, StringComparison.Ordinal);
    }

    /// <summary>A cookie of this site or one of its subdomains (the only ones written to its cookies file).</summary>
    public bool IsSiteCookie(CookieRecord c) => IsSiteHost(c.Domain);

    /// <summary>True when the cookies contain a signed-in session of this site.</summary>
    public bool HasSession(IEnumerable<CookieRecord> cookies)
    {
        var own = cookies.Where(IsSiteCookie).ToList();
        return own.Any(c => _authCookies.Contains(c.Name)) && own.Any(c => _sessionCookies.Contains(c.Name));
    }

    /// <summary>The sign-in that can help with a link: X for x.com / twitter.com links, YouTube for everything else.</summary>
    public static LoginSite For(string? url) => UrlTools.IsX(url) ? X : YouTube;

    public override string ToString() => Name;
}
