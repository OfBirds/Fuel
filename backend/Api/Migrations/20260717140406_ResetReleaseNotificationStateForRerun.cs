using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Migrations
{
    /// <summary>
    /// One-time reset of the release-notification bookkeeping so the current release line is
    /// re-announced once on the next deploy — a deliberate test of the new (shared-library)
    /// notification mechanics.
    ///
    /// <para>Deleting these keys makes the notifier treat the line as not-yet-announced, so it
    /// re-sends the current RELEASE_NOTES.md once, then re-records the keys — every deploy after
    /// this one is silent again. This runs exactly once (EF migration history), so it cannot loop;
    /// the re-send is self-limiting. Only prod actually emails (NOTIFY_ON_DEPLOY); staging is idle.</para>
    /// </summary>
    public partial class ResetReleaseNotificationStateForRerun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM \"SystemSettings\" " +
                "WHERE \"Key\" IN ('last_notified_version', 'last_notified_notes_hash');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible by nature: the deleted bookkeeping can't be reconstructed. No-op down.
        }
    }
}
