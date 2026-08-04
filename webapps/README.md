# Mini apps (bots/webapps)

The server does not host mini apps and never invents a URL for them. A mini app is
an ordinary HTTPS page hosted by the bot's own developer; the owner registers its
URL through BotFather, and the server only validates requests and hands that URL
to clients. See https://corefork.telegram.org/api/bots/webapps .

`index.html` here is a sample mini app used to test the flow end to end. It shows
the `initData` the client passed in and has a button that calls
`Telegram.WebApp.sendData`, which the client turns into `messages.sendWebViewData`
so the bot receives `messageActionWebViewDataSentMe`. Host it anywhere over HTTPS
and point a bot at it with the commands below.

## Registering a URL (as the bot owner, in BotFather)

**Main mini app** — the "Open App" button on the bot's profile, opened by
`messages.requestMainWebView`:

```
/mybots -> pick bot -> Bot Settings -> Configure Mini App -> send the https URL
```

`/empty` there disables it again. Setting a URL flips `BotHasMainApp` on the bot's
user read model, which is what makes clients show the button.

**Named web apps** — direct links like `t.me/yourbot/app`, opened by
`messages.requestAppWebView`:

```
/newapp     create a web app (pick bot, title, description, URL, short name)
/myapps     list every web app you own
/listapps   list the web apps of one bot
/editapp    edit title / description / URL of an app (by its t.me link)
/deleteapp  delete an app (by its t.me link)
```

Short names follow BotFather's rule: 3-30 characters, `a-zA-Z0-9_`.

## How the URL is resolved

`messages.requestWebView` / `requestSimpleWebView` / `requestAppWebView` /
`requestMainWebView` resolve it in this order:

1. the `url` the client passed — only allowed in the chat with the bot itself;
2. for a named app: the `url` of the matching `bot_apps` row (`/newapp`);
3. otherwise: the bot's `MainAppUrl` ("Configure Mini App").

If the owner set nothing, the request fails (`BOT_WEBVIEW_DISABLED` /
`BOT_INVALID` / `BOT_APP_INVALID`) rather than pointing the client at a guessed
address. HTTPS is required throughout — clients refuse to open a webview over
plain HTTP.

The only server-side setting is the session lifetime:

```bash
App__WebApps__SessionTimeoutSeconds=180
```

Clients call `messages.prolongWebView` every 60 seconds while a view is open; the
default allows a couple of missed beats before the session lapses and the client
is told to close the view.

## Known limitations

- BotFather only receives text messages in this server (media is not routed to
  it), so `/newapp` does not ask for the 640x360 photo or the demo GIF that
  upstream BotFather requests, and `/editapp` has no photo/GIF steps.
- `messages.editInlineBotMessage` is still unimplemented, so a mini app cannot
  edit a message it previously sent through inline mode.
