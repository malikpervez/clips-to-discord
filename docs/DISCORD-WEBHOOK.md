# Discord webhook guide

## Required permission

The Discord account creating the webhook needs permission to manage webhooks in the server or channel. Discord identifies **Manage Webhooks** as the permission used to create, edit, and delete channel webhooks.

Official references:

- [Intro to Webhooks](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks)
- [Server Integrations Page](https://support.discord.com/hc/en-us/articles/360045093012-Server-Integrations-Page)
- [Channel Permissions Settings 101](https://support.discord.com/hc/en-us/articles/10543994968087-Channel-Permissions-Settings-101)

## Create the webhook

1. Open **Server Settings → Integrations → Webhooks**.
2. Create a new webhook.
3. Give it a recognizable name such as `ClipCord`.
4. Select the text channel that should receive clips.
5. Copy the webhook URL and paste it directly into the app.

Do not append `/github` to the URL. That suffix is only for Discord's special GitHub webhook format and is not used by this app.

Both Discord URL forms are supported: `/api/webhooks/...` and versioned `/api/v10/webhooks/...`. HTTPS and the Discord host allow-list are still required.

## Test and rotate

Use **Test webhook** in the app. A successful test posts a short connection message to the selected channel.

If a webhook URL is pasted into a public issue, screenshot, chat, stream, or source file:

1. Delete or regenerate that webhook immediately in Discord.
2. Open the app's settings.
3. Paste the replacement URL and test it.
4. Save the settings.

Never include a real webhook URL when reporting a bug. Redact both the numeric webhook ID and secret token.
