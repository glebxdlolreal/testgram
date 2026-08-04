# Mini apps (bots/webapps)

The server does not host mini apps — it only validates requests and tells clients
which URL to load. See https://corefork.telegram.org/api/bots/webapps .

`index.html` is a minimal mini app used to test the flow end to end. It shows the
`initData` the client passed in and has a button that calls
`Telegram.WebApp.sendData`, which the client turns into `messages.sendWebViewData`
so the bot receives `messageActionWebViewDataSentMe`.

## Where the URL comes from

`messages.requestWebView` / `requestSimpleWebView` / `requestAppWebView` /
`requestMainWebView` resolve the URL in this order:

1. the `url` the client passed (only allowed in the chat with the bot itself);
2. the `url` field of a matching row in the `bot_apps` Mongo collection;
3. `App:WebApps:BaseUrl` + `/webapp/{botId}` (or `/webapp/{botId}/{shortName}`).

The base URL defaults to `https://testgram.xie.su` and is configurable:

```bash
App__WebApps__BaseUrl=https://testgram.xie.su
App__WebApps__SessionTimeoutSeconds=180
```

HTTPS is required — clients refuse to open a webview over plain HTTP, and
`requestWebView` rejects non-HTTPS URLs with `URL_INVALID`.

## Deploying this page

Serve it so that the path matches what the server hands out, e.g. for bot id
`2010002` the default URL is `https://testgram.xie.su/webapp/2010002`:

```bash
# on the host serving testgram.xie.su
mkdir -p /var/www/testgram.xie.su/webapp/2010002
cp webapps/index.html /var/www/testgram.xie.su/webapp/2010002/index.html
```

To point a bot at its own page instead, add a row to `bot_apps`:

```javascript
db.bot_apps.insertOne({
  bot_id: NumberLong("2010002"),
  app_id: NumberLong("1"),
  access_hash: NumberLong("12345"),
  short_name: "demo",
  title: "Demo App",
  description: "Testgram mini app demo",
  url: "https://testgram.xie.su/webapp/demo",
  hash: NumberLong("1"),
  request_write_access: false,
  has_settings: false,
  inactive: false
});
```

`bots.setBotInfo`-style state decides whether the profile shows an "Open App"
button: `messages.requestMainWebView` requires `BotHasMainApp` on the bot's user
read model.
