# WhatsApp alerts — setup

BroilIQ sends farm alerts and an evening summary over the **WhatsApp Business Cloud API** (Meta).
Until it is connected, the app runs in **test mode**: everything is evaluated and recorded in each
user's message history (status *Simulated*), but nothing is delivered.

## What gets sent

| Alert | When | De-duplicated per |
|---|---|---|
| Death spike | Latest day's deaths ≥ 5 and ≥ 3× the previous 7-day average | batch + day |
| Heat stress | Today's max ≥ 35 °C, or average ≥ age target + 6 °C | batch + day |
| Chilling | Day ≤ 14 and today's min ≤ age target − 6 °C | batch + day |
| Feed low | Stock covers ≤ 2 days at the last 3 days' usage | batch + day |
| Vaccination due | Day before ND+IB (d6), IBD (d13), ND booster (d21) | batch + dose |
| Vaccination not logged | 2+ days past a dose with too few vaccinations logged | batch + dose |
| Missing record | After 8 PM, no daily record for today | batch + day |
| Daily summary | At the user's chosen hour (default 7 PM) | batch + day |

Each user chooses which of these they want under **Settings → WhatsApp Alerts**, gives their number
and ticks the consent box. Alerts only cover the houses that user can see, in their language
(English / Telugu). An alert is never sent twice to the same person.

The checks run every 15 minutes (`WhatsApp:CheckIntervalMinutes`), in farm time (`WhatsApp:TimeZone`,
default `Asia/Kolkata`).

## 1. Set up WhatsApp Business (one time)

1. In **Meta Business Manager**, create a WhatsApp Business Account and add a phone number that is
   **not** already used on the normal WhatsApp app. Verify the business.
2. In the **Meta developer app** (WhatsApp product), note the **Phone number ID**.
3. Create a **System User** with the `whatsapp_business_messaging` permission and generate a
   **permanent access token** (temporary tokens expire in 24 h).

## 2. Create the two message templates

In **WhatsApp Manager → Message templates**, create each template **twice** — once with language
**English** (`en`) and once with **Telugu** (`te`) — using the *same name*. Category: **Utility**.
The number and order of the `{{n}}` variables must match exactly.

### `broiliq_alert` — 3 variables

| # | Filled with | Example |
|---|---|---|
| {{1}} | House · batch · day — what happened | House 2 · AMR-26-08 · Day 24 — Deaths spiked: 42 birds on 22 Jun |
| {{2}} | Details | That is 3.5x the recent daily average of 12. Total mortality is now 5.53%. |
| {{3}} | What to do | Check the shed now, remove dead birds… |

English body:
```
⚠️ {{1}}

{{2}}

What to do: {{3}}

— BroilIQ
```

Telugu body:
```
⚠️ {{1}}

{{2}}

ఏమి చేయాలి: {{3}}

— BroilIQ
```

### `broiliq_daily_summary` — 6 variables

| # | Filled with | Example |
|---|---|---|
| {{1}} | House · batch · day | House 1 · AMR-26-07 · Day 31 |
| {{2}} | Deaths today | 4 today (total 1.40%) |
| {{3}} | Average weight | 1554 g (standard 1486 g) |
| {{4}} | FCR | 1.429 (standard 1.56) |
| {{5}} | Feed left | 1,200 kg (~2.5 days) |
| {{6}} | Status line | Flock score 96/100 — all on track. |

English body:
```
📊 Daily summary — {{1}}

Deaths: {{2}}
Avg weight: {{3}}
FCR: {{4}}
Feed left: {{5}}

{{6}}

— BroilIQ
```

Telugu body:
```
📊 రోజువారీ సారాంశం — {{1}}

మరణాలు: {{2}}
సగటు బరువు: {{3}}
FCR: {{4}}
మిగిలిన మేత: {{5}}

{{6}}

— BroilIQ
```

Wait for both languages of both templates to show **Approved**.

## 3. Switch it on for the client (Super Admin)

WhatsApp alerts are a per-client feature. Each client sends from **its own** WhatsApp Business
number, so steps 1–2 are done in that client's Meta Business account.

1. Sign in as the Super Admin → **Clients** → open the client → **Features → WhatsApp Alerts**.
2. Tick **Enabled for this client**, enter the **Phone number ID**, the business number farmers will
   see, and paste the **permanent access token**. Save.
3. Ask the client to open **Settings → WhatsApp Alerts** and press **Send test message**. The history
   shows *Sent*, or *Failed* with WhatsApp's reason (for example a template that isn't approved yet).

The token is encrypted before it is stored and is never shown again (only its last 4 characters).
Enabled without an ID/token = test mode for that client (messages recorded as *Simulated*).

Clients without the feature see a **Request this feature** card; requests appear at the top of the
Super Admin's **Clients** page. Switching the feature on approves the request; **Decline** sends the
client a note.

### Platform settings (appsettings / environment variables)

Optional, defaults shown: `WhatsApp:Provider=Meta` (`Log` puts the whole platform in test mode —
use it on dev/staging copies of a real database), `WhatsApp:AlertsEnabled=true`,
`WhatsApp:CheckIntervalMinutes=15`, `WhatsApp:TimeZone=Asia/Kolkata`, `WhatsApp:Meta:ApiVersion=v23.0`,
`WhatsApp:Templates:Alert=broiliq_alert`, `WhatsApp:Templates:DailySummary=broiliq_daily_summary`.

Saved tokens and API keys are encrypted with ASP.NET Data Protection keys kept in
`DataProtection:KeysPath` (default `App_Data/keys`). Back that folder up and share it between servers
if you run more than one; if it is lost, re-enter each client's token and API key.

## Costs

Meta charges per message ("utility" conversations), priced per country; check Meta's current
WhatsApp pricing for India. Roughly: one summary per batch per day plus a few alerts per week per
user.
