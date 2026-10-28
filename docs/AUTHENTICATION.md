# Authentication

The Hub can sign browser users in against an OAuth2 provider itself, so no
authenticating proxy has to stand in front of it. It is off by default, and off
the Hub authenticates nobody, as described in [LIMITATIONS.md](LIMITATIONS.md).

## What it covers

With `Hub:Auth:Enabled`, every route needs a session except the ones a machine
calls:

| Route                                                            | Access                                                                  |
|------------------------------------------------------------------|-------------------------------------------------------------------------|
| `/` and the launcher's files                                     | session, otherwise a redirect to the provider, or to the Hub's sign-in page with a [local account](#local-accounts) |
| `/api/status`, `/api/sources`, `/api/analyses`                   | session, otherwise `401`                                                |
| `/reports/`                                                      | session, `401` in the launcher's frame, a redirect when opened directly |
| `POST /api/sources/{sourceId}/acks` and `/acks/revoke`           | session, otherwise `401`, and the ack is taken in the session's name    |
| `/api/findings`                                                  | open, for IDE plugins and CI jobs, unless access control is on          |
| `GET /api/incidents`, `GET /api/incidents/{id}`, `GET /api/correlations` | open, for dashboards, unless access control is on; `POST /api/incidents/refresh` needs a session |
| `POST /api/import/findings`                                      | its own `X-API-Key`, as before                                          |
| `POST /api/import/incidents`                                     | the same `X-API-Key` as the findings import                             |
| `POST /api/import/correlations`                                  | the same `X-API-Key` as the findings import                             |
| `POST /api/import/detections`                                    | the same `X-API-Key` as the findings import                             |
| `POST /api/import/disclosures`                                   | its own `X-API-Key`, `Hub:Disclosures:ImportKey`                        |
| `/api/disclosures`                                               | session, otherwise `401`                                                |
| `GET /share/{token}/{name}`                                      | open, the share link's token is the credential                          |
| `/health/live`, `/health/ready`, `/metrics`                      | open, for probes and the scrape; `/metrics` on `Hub:MetricsPort` if set |

The API answers `401` rather than a redirect because a fetch cannot follow a
redirect to the provider's origin. The launcher reloads itself on a `401`, and
the reload is what sends the browser to sign in again.

The routes left open still serve whoever reaches the port: `/api/findings`
returns every finding of every source, `/api/incidents` and `/api/correlations`
every incident and correlation episode. Keep the network boundary for them, a
NetworkPolicy or an ingress that does not publish those paths. With
`Hub:Access:Enabled`, these reads need a session or a machine token too, and
each caller reaches what its grants cover, see [ACCESS-CONTROL.md](ACCESS-CONTROL.md).

Without `Hub:Auth:RequiredGroups`, any user the provider signs in is let in:
restrict who may use the Hub on the provider side, by assigning the client to a
group or to users. With it, only members of the groups it lists are, see
[Restricting to groups](#restricting-to-groups).

## How it works

The framework's own OAuth2 and cookie handlers, with no package added. The
browser goes through the authorization code flow with PKCE, the Hub exchanges
the code at the token endpoint, then calls the userinfo endpoint with the access
token and records the field named by `Hub:Auth:IdentityClaim` as the user. A
userinfo without that field fails the sign-in rather than opening a nameless
session. A failed sign-in, that one, a user cancelling on the provider's
screen or a user outside the required groups, answers `403` with one line for
every case, sending the user back to the Hub's home page or to its
administrator, and logs the reason as a warning (event `1900`). No token is
kept: the session is an encrypted `hub_session` cookie, `Secure`, `HttpOnly`,
`SameSite=Lax`, valid 8 hours and renewed while in use, or not renewed under
`Hub:Auth:RequiredGroups`. There is no sign-out button.

The signed-in user is what the topbar shows and what a run records as
`requested_by`. `Hub:Analysis:IdentityHeader` is ignored for a signed-in user,
since any client can send that header.

The [ack relay](API.md#ack-relay) holds the caller to a stricter rule than a run
does. A run records that header as a claim nobody verified. The relay spends a
daemon's write key in the caller's name, so it takes the signed-in user, and it
takes the header only when `Hub:AckRelay:TrustIdentityHeader` is `true`, the
setting for a Hub behind a proxy that writes the header itself and strips the
one a client sent. With a session the header is ignored there too. With neither
a session nor that setting the relay answers `403` to everyone, see
[CONFIGURATION.md](CONFIGURATION.md#the-ack-credential). Whoever is named may
ack on every source that relays, see
[LIMITATIONS.md](LIMITATIONS.md#what-the-hub-does-not-authenticate), unless access
control is on, see [ACCESS-CONTROL.md](ACCESS-CONTROL.md#acknowledgments).

Both relay routes also judge where the request comes from. A browser that sends
`Sec-Fetch-Site: same-origin` passes, and every other value is a `403`,
`cross-site` and `same-site` and the `none` a typed address carries alike. A
caller that sends no such header at all passes, because curl, a CI job and an
IDE plugin do not set it, and refusing them would close the relay to everything
but a browser. What holds that last case is the content type: the Hub takes a
JSON content type and nothing else, which a cross-origin form cannot produce and
a cross-origin fetch cannot send without a preflight the Hub never answers. A
Hub reachable from a browser session should still sit behind a proxy that does
not forward arbitrary origins.

The keys that encrypt the cookie live in a `keys` directory next to
`Hub:DatabasePath`, on the data volume, so a restart does not sign everyone out.
The Hub logs at startup that no XML encryptor is configured: the keys sit
unencrypted on the volume, which holds the database under the same trust.

TLS usually ends at the ingress. The Hub trusts `X-Forwarded-Proto` from any
peer so the redirect URI it sends reads `https`. Only the scheme is taken from
the forwarded headers, until `Hub:Auth:TrustedProxyNetworks` names the proxy:
from then on `X-Forwarded-Proto` and `X-Forwarded-For` are both trusted, from
those networks only and one hop deep, so the Hub sees each client's own address
and a client that talks to it directly cannot forge one.

## Settings

| Setting                            | Default                | Validation                                                                   |
|------------------------------------|------------------------|------------------------------------------------------------------------------|
| `Hub:Auth:Enabled`                 | `false`                | Nothing below is required while off, and `RequiredGroups` must then be empty |
| `Hub:Auth:AuthorizationEndpoint`   | none                   | Absolute HTTPS (HTTP on loopback only), no credentials or fragment           |
| `Hub:Auth:TokenEndpoint`           | none                   | Same                                                                         |
| `Hub:Auth:UserInformationEndpoint` | none                   | Same                                                                         |
| `Hub:Auth:ClientId`                | none                   | Required                                                                     |
| `Hub:Auth:ClientSecret`            | none                   | Required, supplied through a Secret                                          |
| `Hub:Auth:Scopes`                  | `openid profile email` | Space-separated                                                              |
| `Hub:Auth:IdentityClaim`           | `email`                | The userinfo field recorded as the user, a display name                      |
| `Hub:Auth:SubjectClaim`            | `sub`                  | The authorization key, read only with access control: `sub`, `oid`, `uuid` or `account_id` |
| `Hub:Auth:GroupsClaim`             | `groups`               | The userinfo field of the groups, read with `RequiredGroups` or access control |
| `Hub:Auth:RequiredGroups`          | none                   | No blank entry, refused while `Enabled` is off, see below                    |
| `Hub:Auth:TrustedProxyNetworks`    | none                   | CIDR networks of the proxy alone (`10.42.200.0/28`) whose forwarded headers are trusted; terminal sign-in and local accounts need it |

Declare `https://<hub host>/auth/callback` as the client's redirect URI, and
make the client confidential.

Under Helm:

```yaml
hub:
  auth:
    enabled: true
    authorizationEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/auth
    tokenEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/token
    userInformationEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/userinfo
    clientId: perf-sentinel-hub
    clientSecretName: perf-sentinel-hub-oauth
    clientSecretKey: client-secret
    identityClaim: preferred_username
```

The secret reaches the pod as `Hub__Auth__ClientSecret`, never through the
values.

## Providers

The endpoints below come from each provider's documentation. The Hub's tests
play the provider with a fake one.

| Provider        | Authorization, token, userinfo                                                                                                            | Scopes                 | `IdentityClaim`      | `SubjectClaim` |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------|------------------------|----------------------|----------------|
| Keycloak        | `https://<host>/realms/<realm>/protocol/openid-connect/auth`, `.../token`, `.../userinfo`                                                 | `openid profile email` | `preferred_username` | `sub`          |
| Entra ID        | `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize`, `.../oauth2/v2.0/token`, `https://graph.microsoft.com/oidc/userinfo`  | `openid profile email` | `email`              | `sub`          |
| Google          | `https://accounts.google.com/o/oauth2/v2/auth`, `https://oauth2.googleapis.com/token`, `https://openidconnect.googleapis.com/v1/userinfo` | `openid profile email` | `email`              | `sub`          |
| GitLab          | `https://<host>/oauth/authorize`, `https://<host>/oauth/token`, `https://<host>/oauth/userinfo`                                           | `openid profile email` | `preferred_username` | `sub`          |
| Bitbucket Cloud | `https://bitbucket.org/site/oauth2/authorize`, `https://bitbucket.org/site/oauth2/access_token`, `https://api.bitbucket.org/2.0/user`     | `account`              | `username`           | `account_id`   |

Bitbucket is not an OpenID provider: its userinfo is the REST user resource,
and its scopes are the ones granted to the OAuth consumer.

## Restricting to groups

`Hub:Auth:RequiredGroups` lists the groups whose members may use the Hub, and
belonging to one of them is enough. At sign-in the Hub reads the userinfo field
named by `Hub:Auth:GroupsClaim`, a list of strings or a single string, and
compares each entry with the list: exactly, case included, the names as the
provider sends them. An entry that is not a string is skipped. A userinfo
without the field, or naming none of the groups, fails the sign-in. The user
gets the same `403` line as any other failed sign-in, which does not say why.
The warning (event `1900`) names the user and which of the two it was, never
their groups. Empty, the default, the field is not read at all.

Membership is read at sign-in only. With `RequiredGroups` set the session is not
renewed while in use: it ends 8 hours after sign-in, and the next sign-in reads
the groups again. A member removed at the provider keeps their access until
their session ends, at most 8 hours. The list itself is checked on every
request: a session records the group that let it in, so setting
`RequiredGroups`, or removing a group from it, ends the sessions it no longer
covers once the Hub restarts with it, those opened before the gate included.
Their users go back through the sign-in. Without it the session is renewed as
before.

The list is configuration, deployed with the rest of it, and the Hub has no
screen to change it. Set while `Hub:Auth:Enabled` is off, it stops the Hub at
startup rather than leaving a gate that checks nobody, and the chart's schema
refuses it the same way. Under Helm:

```yaml
hub:
  auth:
    enabled: true
    # The provider's endpoints and client, as above.
    groupsClaim: groups
    requiredGroups:
      - /perf-sentinel-hub
```

Not every provider puts groups in its userinfo:

- **Keycloak** does once the client has a *Group Membership* mapper with
  *Token Claim Name* `groups` and *Add to userinfo* on, on its dedicated scope
  or on a *Default* client scope. On an *Optional* client scope the mapper
  applies only when that scope is requested: add its name to `Hub:Auth:Scopes`.
  With *Full group path* on, a group is sent as its path, `/parent/child`, and
  `RequiredGroups` spells it that way. Off, it is the bare group name.
- **GitLab** lists in `groups` the paths of the groups the user belongs to,
  directly or through a parent group.
- **Entra ID** and **Google** do not. Their userinfo carries the user's name and
  email and no group, and Entra ID's cannot be extended, so `RequiredGroups`
  cannot be used with them as they are. Restrict who signs in on the provider
  side instead.
- **Bitbucket Cloud**'s user resource lists no group either.

## With access control

`Hub:Access:Enabled` decides, inside the sessions `RequiredGroups` lets in, what each
caller reaches. The two are independent and each needs `Hub:Auth:Enabled`; set both. The
model, the routes and machine tokens are in [ACCESS-CONTROL.md](ACCESS-CONTROL.md). What
changes for sign-in:

- **The subject decides, the identity displays.** The userinfo field
  `Hub:Auth:SubjectClaim` names (default `sub`, see the provider table) is the key that
  `Hub:Access:Admins` and internal-group members are compared with. `IdentityClaim`
  keeps its meaning: the name shown, recorded in `requested_by` and an ack's `by`, never
  compared. A userinfo whose subject field is missing, not a string, blank or longer than
  256 characters fails the sign-in.
- **Reserved prefixes.** A subject or an identity starting with `local:` or `machine:`,
  case ignored, fails the sign-in. With access off, the identity is only a display name
  and such a value is still accepted.
- **Groups.** `GroupsClaim` is read once per sign-in and feeds both gates, by the rules
  above. The session keeps only the provider groups a grant or `Hub:Access:AdminGroups`
  names, and never a value starting with `internal:`. The names it dropped are logged at
  Debug (event `1902`), which shows how the provider spells them. A grant newly given to a
  provider group reaches a signed-in user at their next sign-in.
- **Provider admin groups.** `AdminGroups` may name a provider group. On a provider where
  users create groups of any name (gitlab.com, and self-managed GitLab by default), anyone
  can create a top-level group of that name: such an entry is safe only with
  `RequiredGroups` limiting sign-in to the organisation's members, and the group named by
  a full path under the organisation's own group. `Admins` is not exempt from
  `RequiredGroups`: those people still need a listed group to sign in.
- **The session no longer renews**, as under `RequiredGroups`: it ends 8 hours after
  sign-in. A session opened before access control was turned on carries no subject and
  goes back through the sign-in.
- **`Hub:Analysis:IdentityHeader` is never read**, for status, `requested_by` and acks
  alike, and `Hub:AckRelay:TrustIdentityHeader` must stay `false`.
- **`reader` is not read-only**: the built-in role starts analyses.

Every refusal answers the same `403` line and logs event `1900` with its reason.

## Terminal sign-in

With access control on, `perf-sentinel login` gets the developer a personal token for
the CLI, without copying a secret. It follows the device authorization flow of RFC 8628,
run by the Hub itself:

1. The CLI asks the Hub for a code (`POST /auth/device`) and prints it with the Hub's
   `/device` page.
2. The developer opens `/device` in a browser signed in to the Hub and **types** the
   code. No link carries it: a code someone else sends cannot arrive filled in.
3. The page shows where and when the code was requested, the address and the time, who
   is signed in and when the token will expire. When the request came from another
   network than the browser's, it says so, and **Approve** needs a second click
   (**Approve anyway**). A code from another network is the shape of a phishing attempt.
4. Once approved, the CLI's next poll (`POST /auth/device/token`) receives the token.

**Who approves.** Only a browser session approves, verifies or manages tokens; a bearer
token never does, so a token cannot mint another. A session with less than an hour left
is sent back through the provider first, so every token lives at least an hour.

**What the token carries.** The approving session's subject, identity and provider
groups, frozen at approval, and the `RequiredGroups` group that admitted it. Internal
groups, admin lists and `RequiredGroups` are read on every request, as for a session.
A terminal token is **never admin**, even when an admin approves it: it holds the
person's grants, which `hub ack` and `hub analyze` need, and the whole-Hub powers stay
with a browser session. A phished code therefore hands over at most the victim's
grants.

**Lifetime and revocation.** A token expires when the session that approved it does,
8 hours after that sign-in at most; there is no refresh token. A person holds at most 10
live tokens, and the 11th drops the oldest. `perf-sentinel logout` revokes its token
(`POST /auth/revoke`). The launcher lists the caller's own tokens on Your access, and
everyone's on an admin's Coverage screen, each with a **Revoke** button. Revocation applies to the next request. Only a
token's SHA-256 is stored, never the token. Under `RequiredGroups`, a token whose
admitting group has been removed from the list stops working at the next restart, as a
session does.

**It needs `Hub:Auth:TrustedProxyNetworks`.** Behind an ingress, without that list,
every request carries the ingress controller's address: the page would show the same
address for every login, an attacker's included, and the per-address limit on pending
codes would block every terminal sign-in for one noisy client. The Hub cannot tell
whether a proxy fronts it, so with the list empty it maps none of the device-flow
routes, logs event `2014` at startup and the CLI reports that the Hub offers no terminal
sign-in. The token list and revocation stay available. A Hub with no proxy in front
lists `127.0.0.1/32`.

List the narrowest range that holds the proxy and nothing else. Any address in the list
can set the client address the Hub records, so a range shared with other workloads, such
as the whole cluster's pod CIDR, lets any pod in it forge that address: it gets past the
per-address limit and can borrow a victim's address to hide the other-network warning.
Give the ingress controller's pods a range of their own, or let a NetworkPolicy admit to
the Hub only those pods within the listed range.

```yaml
hub:
  auth:
    trustedProxyNetworks: ["10.42.200.0/28"]   # the ingress controller's pods alone
```

The ingress must itself see the client's address. A LoadBalancer Service with
`externalTrafficPolicy: Cluster` replaces it with a node's address before the ingress:
every login then seems to come from a handful of addresses, the approval page never
warns of another network, and the per-address limit is shared by everyone behind a
node. Use `externalTrafficPolicy: Local`, or the PROXY protocol between the load
balancer and the ingress.

**Limits.** At most 200 pending codes, 10 per client address (an IPv6 client counts by
its /64); a code lasts 10 minutes and is used once. The CLI polls every 5 seconds, and
5 more each time it polls too early (`slow_down`). Ten mistyped or unknown codes in 10
minutes pause a person's approvals for the rest of those 10 minutes. Pending codes live
in memory, so a restart cancels the logins in flight.

## Local accounts

With access control on, a local account is the way in when the provider is down.
Signing in through the provider stays the normal path: declare one or two break-glass
accounts, keep their passwords in a vault, and change them after every use.

**Declaring one.** `Hub:Access:LocalAccounts` lists `{ Name, PasswordHash }`. The account
signs in as `local:<name>`, its subject and its identity alike, and `Hub:Access:Admins`
or an internal group's `Members` must list `local:<name>`: an account that would reach
nothing stops the Hub at startup. The name matches `^[a-z0-9][a-z0-9._-]{0,63}$`. The
hash is the line `PerfSentinelHub hash-password` prints (see
[OPERATIONS.md](OPERATIONS.md#local-accounts-break-glass)), delivered from a Secret, never
through the values. A provider subject or identity starting with `local:` fails the
sign-in, so no provider user can pass for a local account.

**The sign-in page.** With a local account declared, a page request without a session goes
to the Hub's own `/auth/sign-in` page instead of the provider. It offers **Sign in with
single sign-on** first and, folded below it, **Sign in with a local account**. The Hub
serves that page without contacting anyone, so it opens while the provider is down; the
provider is reached only when the user clicks single sign-on, and the user comes back to
the page they asked for. The API still answers `401`, and the launcher reloads into the
page. With no local account declared nothing changes: a page goes straight to the
provider.

**The session.** A fixed 8 hours from sign-in, never renewed. It carries no provider
group: its groups are the internal groups that list `local:<name>`, read at each request,
and it is admin when `Admins` lists it or `AdminGroups` names one of those groups.
`Hub:Auth:RequiredGroups` does not apply to it: the account is declared in the same
configuration, and is the way in when the provider is not. It may approve a terminal
sign-in, whose token ends with the session.

**Rotation ends the sessions.** A session carries a stamp of the configured hash. A new
hash, once the pod restarts, ends every session of that account, and removing the account
ends them at their next request. Terminal tokens the account approved live until they
expire, 8 hours at most; an admin revokes them from the Coverage screen.

**Guessing.** The Hub hashes one password at a time, PBKDF2-HMAC-SHA256 at 600,000
iterations, and a password holds 15 to 1,024 characters. An unknown name costs the same
hash as a declared one and gets the same answer, `403` "The name or the password is
wrong.", byte for byte, so neither the answer nor its timing tells names apart.

- **Backoff.** A client address and a name get five failures free, then wait 30 seconds,
  doubled at each further failure up to 15 minutes; a success clears it. There is no
  lock: an attacker slows only their own address, never the operator's.
- **Budget.** One address hashes at most 5 times a minute, whatever the names.
- Beyond either, `429` with `Retry-After`, without hashing. A request waits up to 10
  seconds for the hashing slot, with at most 8 waiting, then gets `503` and
  `Retry-After: 1`.
- The throttle lives in memory, and a restart clears it.
- The form is accepted from the Hub's own page only: a `Sec-Fetch-Site` other than
  `same-origin`, or a foreign `Origin`, gets `403`.

**It needs `Hub:Auth:TrustedProxyNetworks`.** Without it every client behind the ingress
shares one address, and anyone could keep the break-glass account throttled. The Hub
refuses to start with a local account and an empty list. A Hub with no proxy in front
lists `127.0.0.1/32`.

**Logging.** Every local sign-in logs event `2020` at Warning, with the account and the
client address. Every refused attempt logs event `2021` at Warning, with the account,
the address and the consecutive failures. An undeclared name is logged as
`(unknown name)`: it may be a password typed in the wrong field. `429` and `503` answers
are not logged.

```yaml
hub:
  auth:
    trustedProxyNetworks: ["10.42.200.0/28"]   # the ingress controller's pods alone
  access:
    adminGroups: ["/hub-admins", "internal:break-glass"]
    groups:
      - { name: break-glass, members: ["local:ops"] }
    localAccounts:
      - { name: ops, secretName: perf-sentinel-hub-break-glass, secretKey: ops }
```
