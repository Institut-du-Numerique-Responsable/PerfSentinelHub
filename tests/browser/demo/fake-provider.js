// An OAuth2 provider just real enough for the access-on Hub to sign three people in.
// The Hub needs an authorization page, a token endpoint and a userinfo endpoint and
// nothing else: it reads no ID token and fetches no discovery document. The page lists
// the people as buttons, so a spec signs in as one by clicking their name, the way a
// provider's own account chooser works.
//
// The code, the access token and the person's key are the same string. Nothing here
// is a secret: it listens on loopback for the length of one demo run.
//
// Usage: node fake-provider.js <port>
const http = require("node:http");

const [port] = process.argv.slice(2);
if (!port) {
    console.error("usage: node fake-provider.js <port>");
    process.exit(2);
}

// The userinfo each person gets. The groups are spelled the way Keycloak sends them
// with "Full group path" on, which is what global-setup's grants name.
const PEOPLE = {
    ada: {sub: "3f1c9e52-7b4d-4a8e-9d26-5c0b8e1f7a43", email: "ada@example.com", groups: ["/hub-admins"]},
    sam: {
        sub: "8a2d4f60-1c3e-4b7a-a95f-2e6d0c9b4f18", email: "sam@example.com",
        groups: ["/teams/shop", "/teams/orders"]
    },
    noor: {sub: "c75e0b19-9f2a-4d63-8e41-7a3b6d2c5e90", email: "noor@example.com", groups: ["/teams/marketing"]}
};

const escape = (text) => text.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

http.createServer((req, res) => {
    const url = new URL(req.url, `http://127.0.0.1:${port}`);
    if (url.pathname === "/authorize") {
        const back = (person) => {
            const target = new URL(url.searchParams.get("redirect_uri") ?? "");
            target.searchParams.set("code", person);
            target.searchParams.set("state", url.searchParams.get("state") ?? "");
            return target.toString();
        };
        res.writeHead(200, {"content-type": "text/html; charset=utf-8"});
        res.end(`<!doctype html><title>Fake provider</title><h1>Sign in as</h1>${
            Object.entries(PEOPLE).map(([person, info]) =>
                `<p><a role="button" href="${escape(back(person))}">${escape(info.email)}</a></p>`).join("")}`);
        return;
    }
    if (url.pathname === "/token" && req.method === "POST") {
        let body = "";
        req.on("data", (chunk) => (body += chunk));
        req.on("end", () => {
            const code = new URLSearchParams(body).get("code") ?? "";
            const known = Object.hasOwn(PEOPLE, code);
            res.writeHead(known ? 200 : 400, {"content-type": "application/json"});
            res.end(JSON.stringify(known
                ? {access_token: code, token_type: "Bearer", expires_in: 3600}
                : {error: "invalid_grant"}));
        });
        return;
    }
    if (url.pathname === "/userinfo") {
        const person = (req.headers.authorization ?? "").replace(/^Bearer /, "");
        req.resume();
        if (!Object.hasOwn(PEOPLE, person)) {
            res.writeHead(401);
            res.end();
            return;
        }
        res.writeHead(200, {"content-type": "application/json"});
        res.end(JSON.stringify(PEOPLE[person]));
        return;
    }
    req.resume();
    res.writeHead(404);
    res.end();
}).listen(Number(port), "127.0.0.1", () => console.log(`fake provider on http://127.0.0.1:${port}`));
