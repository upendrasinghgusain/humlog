# LG remote key codes (LG 43UM7600PTA, webOS 4.5)

Open `https://humlog.upendragusain.co.uk/tv/keys.html?key=<TV key>` in the TV browser, press each button and note the number shown.

| Button | Expected | Observed |
|---|---|---|
| Left | 37 | |
| Right | 39 | |
| OK | 13 | |
| Play | 415 | |
| Pause | 19 | |
| Back | 461 | |
| Up (start again from newest) | 38 | |
| Red (start again from newest) | 403 | |
| Down (shuffle everything) | 40 | |
| Green (shuffle everything) | 404 | |

If a code differs, update the `keydown` switch in `poc/web/app.js`.
