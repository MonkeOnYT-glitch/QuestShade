# QuestShade preset server

The public preset browser (Effects > Preset browser in the mod). A Cloudflare Worker with a D1 database, free tier.

Deploy (once):

    npx wrangler login
    npx wrangler d1 create questshade-presets      # paste the database_id into wrangler.toml
    npx wrangler d1 execute questshade-presets --remote --file=schema.sql
    npx wrangler secret put ADMIN_KEY              # any long random string, for hiding presets
    npx wrangler secret put DISCORD_WEBHOOK        # optional: a #mod-log webhook, new shares + reports
    npx wrangler deploy

Hide a preset: `curl -X DELETE -H "Authorization: Bearer ADMIN_KEY" https://<worker>/api/presets/<id>`
Un-hide: `curl -X POST -H "Authorization: Bearer ADMIN_KEY" https://<worker>/api/presets/<id>/restore`
