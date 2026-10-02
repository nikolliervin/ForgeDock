// Opt-in integration check against the local Keycloak. Creates and removes a temporary
// operator; the real operator's password and authenticators are never changed.
import { chromium } from "../web/node_modules/playwright/index.mjs";
import { spawn, execFileSync } from "node:child_process";
import { randomUUID, randomBytes, createHmac } from "node:crypto";
import { readFileSync, writeFileSync, openSync, closeSync } from "node:fs";
import { resolve } from "node:path";
import assert from "node:assert/strict";

const root = process.env.ForgeDock__RuntimePath;
assert(
  root && process.env.ForgeDock__Auth__Mode === "keycloak",
  "Select local Keycloak mode first.",
);
const issuer = new URL(process.env.ForgeDock__Oidc__Authority);
const keycloak = issuer.origin;
const dashboard = process.env.ForgeDock__Oidc__PublicOrigin;
const factor = process.env.ForgeDock__Keycloak__SecondFactor ?? "totp";
const id = randomUUID();
let otpSecret;
function totp(secret, step = 0) {
  // Keycloak posts its raw secret; the QR code contains its Base32 encoding.
  const key = Buffer.from(secret, "utf8");
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000) + step));
  const hash = createHmac("sha1", key).update(counter).digest();
  const offset = hash.at(-1) & 15;
  return ((hash.readUInt32BE(offset) & 0x7fffffff) % 1000000)
    .toString()
    .padStart(6, "0");
}
const username = "verification-" + id;
const initialPassword = randomBytes(24).toString("base64url");
const newPassword = randomBytes(24).toString("base64url");
const proxyFile = resolve(root, "keycloak/proxy/Caddyfile");
const originalProxy = readFileSync(proxyFile, "utf8");
let api,
  browser,
  adminToken,
  subject,
  userCreated = false;
async function admin(path, method = "GET", body) {
  const response = await fetch(keycloak + "/admin/realms/forgedock" + path, {
    method,
    headers: {
      Authorization: "Bearer " + adminToken,
      "Content-Type": "application/json",
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  assert(response.ok, "Keycloak admin request failed: " + response.status);
  return response;
}
try {
  const login = await fetch(
    keycloak + "/realms/master/protocol/openid-connect/token",
    {
      method: "POST",
      body: new URLSearchParams({
        grant_type: "password",
        client_id: "admin-cli",
        username: process.env.ForgeDock__Keycloak__AdminUsername,
        password: process.env.ForgeDock__Keycloak__AdminPassword,
      }),
    },
  );
  assert(login.ok, "Keycloak administrator authentication failed.");
  adminToken = (await login.json()).access_token;
  const createdUser = await admin("/users", "POST", {
    username,
    enabled: true,
    firstName: "Verification",
    lastName: "Operator",
    requiredActions: [
      "UPDATE_PASSWORD",
      factor === "totp" ? "CONFIGURE_TOTP" : "webauthn-register",
    ],
    credentials: [
      { type: "password", value: initialPassword, temporary: true },
    ],
  });
  subject = new URL(createdUser.headers.get("location")).pathname
    .split("/")
    .at(-1);
  userCreated = true;
  const logfile = openSync(
    resolve(root, "keycloak/api-verification.log"),
    "w",
    0o600,
  );
  api = spawn(
    "dotnet",
    ["src/ForgeDock.Api/bin/Debug/net10.0/ForgeDock.Api.dll"],
    {
      env: {
        ...process.env,
        ASPNETCORE_URLS: "http://127.0.0.1:5081",
        ForgeDock__Oidc__AllowedSubjects__1: subject,
      },
      stdio: ["ignore", logfile, logfile],
    },
  );
  closeSync(logfile);
  for (let attempt = 0; attempt < 40; attempt++) {
    try {
      if ((await fetch("http://127.0.0.1:5081/health/live")).ok) break;
    } catch {}
    assert(
      api.exitCode === null,
      "Verification API exited. Inspect its private log.",
    );
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  writeFileSync(
    proxyFile,
    originalProxy.replaceAll("127.0.0.1:5080", "127.0.0.1:5081"),
  );
  execFileSync("docker", ["restart", "forgedock-sso-proxy"], {
    stdio: "ignore",
  });
  for (let attempt = 0; attempt < 30; attempt++) {
    try {
      if ((await fetch(dashboard + "/api/auth/config")).ok) break;
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext(); // HTTPS errors are never ignored.
  const page = await context.newPage();
  if (factor === "webauthn") {
    const cdp = await context.newCDPSession(page);
    await cdp.send("WebAuthn.enable");
    await cdp.send("WebAuthn.addVirtualAuthenticator", {
      options: {
        protocol: "ctap2",
        transport: "internal",
        hasResidentKey: true,
        hasUserVerification: true,
        isUserVerified: true,
        automaticPresenceSimulation: true,
      },
    });
  }
  await page.goto(dashboard);
  await page.getByRole("button", { name: "Sign in with SSO" }).click();
  await page.locator("#username").fill(username);
  await page.locator("#password").fill(initialPassword);
  await page.locator("#kc-login").click();
  await page.locator("#password-new").fill(newPassword);
  await page.locator("#password-confirm").fill(newPassword);
  await page.locator("button[type=submit], input[type=submit]").click();
  assert(
    await page.locator('link[href*="forgedock.css"]').count(),
    "ForgeDock portal theme is missing.",
  );
  if (factor === "totp") {
    otpSecret = await page.locator('input[name="totpSecret"]').inputValue();
    await page.locator("#totp").fill(totp(otpSecret));
    const label = page.locator("#userLabel");
    if (await label.count()) await label.fill("Temporary phone authenticator");
    await page.locator("button[type=submit], input[type=submit]").click();
  } else {
    page.once("dialog", (dialog) =>
      dialog.accept("Temporary verification passkey"),
    );
    await page.getByRole("button", { name: /Register/ }).click();
  }
  await page.waitForURL(dashboard + "/dashboard", { timeout: 30000 });
  let session = await context.request.get(dashboard + "/api/session");
  assert.equal(
    session.status(),
    200,
    "Real OIDC code exchange did not create a session.",
  );
  const payload = await session.json();
  assert(payload.csrfToken, "Session is missing its antiforgery token.");
  await page.reload();
  await page.getByRole("button", { name: "Sign out", exact: true }).waitFor();
  const cookie = (await context.cookies()).find(
    (cookie) => cookie.name === "__Host-ForgeDock.Session",
  );
  assert(
    cookie?.secure && cookie?.httpOnly,
    "Session cookie must be Secure and HttpOnly.",
  );
  assert.equal(
    (
      await context.request.post(dashboard + "/api/auth/logout", { data: {} })
    ).status(),
    400,
  );
  assert.equal(
    (
      await context.request.post(dashboard + "/api/auth/logout", {
        data: {},
        headers: { "X-CSRF-Token": payload.csrfToken },
      })
    ).status(),
    204,
  );
  session = await context.request.get(dashboard + "/api/session");
  assert.equal(session.status(), 401);
  if (factor === "totp") {
    await context.clearCookies();
    await page.goto(dashboard);
    await page.getByRole("button", { name: "Sign in with SSO" }).click();
    await page.locator("#username").fill(username);
    await page.locator("#password").fill(newPassword);
    await page.locator("#kc-login").click();
    await page.locator("#otp").waitFor();
    const valid = new Set([-1, 0, 1].map((step) => totp(otpSecret, step)));
    let wrong = "000000";
    while (valid.has(wrong))
      wrong = (Number(wrong) + 1).toString().padStart(6, "0");
    await page.locator("#otp").fill(wrong);
    await page.locator("button[type=submit], input[type=submit]").click();
    await page.locator("#otp").waitFor();
    assert.equal(
      (await context.request.get(dashboard + "/api/session")).status(),
      401,
    );
    // Keycloak rejects reuse of a code consumed during enrollment. Wait for a fresh step.
    await new Promise((resolve) =>
      setTimeout(resolve, 31000 - (Date.now() % 30000)),
    );
    await page.locator("#otp").fill(totp(otpSecret));
    await page.locator("button[type=submit], input[type=submit]").click();
    await page.waitForURL(dashboard + "/dashboard", { timeout: 30000 });
    assert.equal(
      (await context.request.get(dashboard + "/api/session")).status(),
      200,
    );
  }
  console.log(
    "Local Keycloak verified: ForgeDock theme, " +
      factor +
      " enrollment/login, real OIDC callback, session restoration, CSRF protection, and logout.",
  );
} finally {
  await browser?.close();
  if (originalProxy !== readFileSync(proxyFile, "utf8")) {
    writeFileSync(proxyFile, originalProxy);
    execFileSync("docker", ["restart", "forgedock-sso-proxy"], {
      stdio: "ignore",
    });
  }
  if (api && api.exitCode === null) {
    api.kill("SIGTERM");
    await new Promise((resolve) => api.once("exit", resolve));
  }
  if (userCreated) await admin("/users/" + subject, "DELETE");
}
