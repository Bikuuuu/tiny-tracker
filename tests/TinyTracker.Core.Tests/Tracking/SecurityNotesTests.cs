using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Tracking;

// Release notes that mention a security fix (spec §6.2), shaped like winget's, with made-up names.
public class SecurityNotesTests
{
    [Theory]
    [InlineData("- Improved support for disc images.\n- Some bugs and vulnerabilities were fixed.")]
    [InlineData("- CVE-2026-10001 : Example Zip could run a file from a crafted archive.")]
    [InlineData("Bug Fixes\n- Fixes an overflow in the sign-in helper, see GHSA-2x7f-9m4q-r8vw for full details.")]
    [InlineData("- Fix a crash on crafted input in the parser [GHSA-4c6h-p2q9-x3mj]")]
    [InlineData("- Security issue: fixed a remotely triggerable use-after-free in the agent.")]
    [InlineData("- Denial-of-service security fixes: a server can trigger a tight loop.")]
    [InlineData("fixed a security issue with opening files from the network")]
    [InlineData("This release includes a security update.")]
    [InlineData("- Patched a security hole in the updater.")]
    [InlineData("- Fixes security of the sign-in page.")]
    [InlineData("## Security\n- Downloads are checked.")]
    [InlineData("Security:\r\n- Downloads are checked.")]
    [InlineData("**Security**\n- Downloads are checked.")]
    [InlineData("- Security: fixed a use-after-free in the agent.")]
    [InlineData("+ Security: fixed a use-after-free in the agent.")]
    [InlineData("  • Security: updated the TLS library to 3.0.13")]
    [InlineData("• Security\n• Downloads are checked.")]
    [InlineData("1. Security: fixed a use-after-free in the agent.")]
    [InlineData("1. **Security**: fixed a use-after-free in the agent.")]
    [InlineData("- __Security__: updated the TLS library to 3.0.13")]
    [InlineData("  2) Security: updated the TLS library to 3.0.13")]
    [InlineData("> Security: fixed a use-after-free in the agent.")]
    [InlineData("Security: updated the TLS library to 3.0.13")]
    [InlineData("- A VULNERABILITY in the importer was fixed.")]
    [InlineData("- No longer crashes on start, and fixes a security hole.")]
    public void SecurityFix_IsFound(string notes) => Assert.True(SecurityNotes.Mention(notes));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("- Added support for security keys.")]
    [InlineData("- Windows Security now lists the app.")]
    [InlineData("- The security settings page moved.")]
    [InlineData("Security keys\n- Added.")]
    [InlineData("- Cybersecurity training videos added.")]
    [InlineData("- Disabled a plugin in private windows and forced HTTPS on install.")]
    [InlineData("- TLS core upgraded to OpenSSL 3.3.6.")]
    [InlineData("- Applied process mitigation policies on Windows.")]
    [InlineData("- Mentions CVE without a number.")]
    [InlineData("- Fixed the security settings page crash")]
    [InlineData("- Fixed security warning showing twice")]
    [InlineData("+ Security keys are now supported.")]
    [InlineData("• Security keys")]
    [InlineData("1. Security keys are now supported.")]
    [InlineData("1. **Security** keys are now supported.")]
    public void OtherNotes_AreNot(string? notes) => Assert.False(SecurityNotes.Mention(notes));

    // A match stays within its line: one change's words don't join the next one's.
    [Theory]
    [InlineData("- Improved security\n- Bug fixes")]
    [InlineData("- Improved security\r\n- Fixed a crash on start")]
    [InlineData("- Better privacy and security\n  - Issue with dark mode resolved")]
    [InlineData("Various fixes\nSecurity improvements")]
    public void WordsOnTwoLines_AreNot(string notes) => Assert.False(SecurityNotes.Mention(notes));

    // Notes that say there's none.
    [Theory]
    [InlineData("This release contains no security fixes.")]
    [InlineData("- No security issues were found in this release.")]
    [InlineData("- This isn't a security update.")]
    [InlineData("- Not a security fix, just faster.")]
    [InlineData("- Doesn't contain any security updates.")]
    [InlineData("No known vulnerabilities.")]
    [InlineData("Security: none")]
    [InlineData("• Security: none")]
    [InlineData("1. **Security**: none")]
    [InlineData("**Security:** N/A")]
    [InlineData("- Security fixes: nothing this time.")]
    public void NotesSayingThereAreNone_AreNot(string notes) => Assert.False(SecurityNotes.Mention(notes));
}
