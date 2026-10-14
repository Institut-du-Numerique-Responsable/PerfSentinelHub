# Authentication

The Hub can sign browser users in against an OAuth2 provider itself, so no
authenticating proxy has to stand in front of it. It is off by default, and off
the Hub authenticates nobody, as described in [LIMITATIONS.md](LIMITATIONS.md).

## What it covers

With `Hub:Auth:Enabled`, every route needs a session except the ones a machine
calls:

| Route                                                            | Access                                                                  |
|------------------------------------------------------------------|-------------------------------------------------------------------------|
| `/` and the launcher's files                                     | session, otherwise a redirect to the provider                           |
| `/api/status`, `/api/sources`, `/api/incidents`, `/api/analyses` | session, otherwise `401`                                                |
| `/reports/`                                                      | session, `401` in the launcher's frame, a redirect when opened directly |
| `POST /api/sources/{sourceId}/acks` and `/acks/revoke`           | session, otherwise `401`, and the ack is taken in the session's name    |
| `/api/findings`                                                  | open, for IDE plugins and CI jobs, unless access control is on          |
| `POST /api/import/findings`                                      | its own `X-API-Key`, as before                                          |
| `/health/live`, `/health/ready`, `/metrics`                      | open, for probes and the scrape; `/metrics` on `Hub:MetricsPort` if set |

The API answers `401` rather than a redirect because a fetch cannot follow a
redirect to the provider's origin. The launcher reloads itself on a `401`, and
the reload is what sends the browser to sign in again.

The routes left open still serve whoever reaches the port: `/api/findings`
returns every finding of every source. Keep the network boundary for them, a
NetworkPolicy or an ingress that does not publish those paths. With
`Hub:Access:Enabled`, `/api/findings` needs a session or a machine token too, and
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
the forwarded headers.

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
