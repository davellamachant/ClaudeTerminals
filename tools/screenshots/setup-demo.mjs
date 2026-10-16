// Builds a fictional "acme-shop" project for README screenshots: a bare remote, a commit history by
// made-up authors, four worktrees with uncommitted changes, fake saved Claude conversations and an
// isolated Claude Terminals profile. Usage: node setup-demo.mjs <demoRoot>
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(process.argv[2] ?? 'D:\\Demos\\acme');
const MARKER = '.claude-terminals-demo';
const here = path.dirname(fileURLToPath(import.meta.url));

// Only ever delete a folder this script created.
if (fs.existsSync(root)) {
  if (!fs.existsSync(path.join(root, MARKER))) {
    console.error(`${root} exists and wasn't created by this script; refusing to touch it.`);
    process.exit(1);
  }
  fs.rmSync(root, { recursive: true, force: true });
}
fs.mkdirSync(root, { recursive: true });
fs.writeFileSync(path.join(root, MARKER), 'Created by tools/screenshots/setup-demo.mjs; safe to delete.\n');

const DAY = 86_400_000;
const people = {
  maya: ['Maya Chen', 'maya@acme-shop.example'],
  sam: ['Sam Ortiz', 'sam@acme-shop.example'],
  priya: ['Priya Nair', 'priya@acme-shop.example'],
  leo: ['Leo Martin', 'leo@acme-shop.example'],
};

function git(cwd, args, { who = 'maya', daysAgo = 0 } = {}) {
  const [name, email] = people[who];
  const date = new Date(Date.now() - daysAgo * DAY - 3_600_000 * 3).toISOString();
  return execFileSync('git', args, {
    cwd,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: {
      ...process.env,
      GIT_AUTHOR_NAME: name, GIT_AUTHOR_EMAIL: email, GIT_AUTHOR_DATE: date,
      GIT_COMMITTER_NAME: name, GIT_COMMITTER_EMAIL: email, GIT_COMMITTER_DATE: date,
    },
  });
}

function write(dir, files) {
  for (const [rel, content] of Object.entries(files)) {
    const full = path.join(dir, rel);
    fs.mkdirSync(path.dirname(full), { recursive: true });
    fs.writeFileSync(full, content.replace(/^\n/, ''));
  }
}

function commit(dir, message, files, opts) {
  if (files) write(dir, files);
  git(dir, ['add', '-A'], opts);
  git(dir, ['commit', '-q', '-m', message], opts);
}

// ------------------------------------------------------------------ source files

const F = {
  'package.json': (express = '4.19.2', stripe = '16.2.0') => `
{
  "name": "acme-shop",
  "version": "0.4.0",
  "private": true,
  "scripts": {
    "dev": "tsx watch src/server.ts",
    "build": "tsc -p .",
    "test": "vitest run"
  },
  "dependencies": {
    "express": "^${express}",
    "express-session": "^1.18.0",
    "stripe": "^${stripe}"
  },
  "devDependencies": {
    "tsx": "^4.16.0",
    "typescript": "^5.5.0",
    "vitest": "^2.0.0"
  }
}
`,
  'tsconfig.json': `
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "strict": true,
    "jsx": "react-jsx",
    "outDir": "dist"
  },
  "include": ["src", "web", "tests"]
}
`,
  'README.md': (deploy = false) => `
# Acme Shop

A small storefront: an Express API for carts and checkout, and React components for the web client.

## Getting started

\`\`\`sh
npm install
npm run dev
\`\`\`

The API listens on http://localhost:3000.
${deploy ? `
## Deploying

See [docs/deploy.md](docs/deploy.md) for deploying to Fly.io, including secrets,
the Postgres attachment and zero-downtime releases.
` : ''}`,
  'src/server.ts': (webhooks = false) => `
import express from 'express';
import session from 'express-session';
import { cartRoutes } from './routes/cart';
import { paymentRoutes } from './routes/payments';
${webhooks ? "import { webhookRoutes } from './routes/webhooks';\n" : ''}
const app = express();

${webhooks ? "// Stripe needs the raw body to verify signatures, so mount webhooks before the JSON parser.\napp.use('/api/webhooks', webhookRoutes);\n" : ''}app.use(express.json());
app.use(session({ secret: process.env.SESSION_SECRET ?? 'dev-only', resave: false, saveUninitialized: true }));

app.use('/api/cart', cartRoutes);
app.use('/api/payments', paymentRoutes);

const port = Number(process.env.PORT ?? 3000);
app.listen(port, () => console.log(\`acme-shop listening on :\${port}\`));
`,
  'src/routes/cart.ts': `
import { Router } from 'express';

export interface CartItem { sku: string; name: string; unitPrice: number; quantity: number; }
export interface Cart { items: CartItem[]; }

const carts = new Map<string, Cart>();

export function getCart(sessionId: string): Cart {
  if (!carts.has(sessionId)) carts.set(sessionId, { items: [] });
  return carts.get(sessionId)!;
}

export function clearCart(sessionId: string) {
  carts.delete(sessionId);
}

export const cartRoutes = Router();

cartRoutes.get('/', (req, res) => res.json(getCart(req.sessionID)));

cartRoutes.post('/items', (req, res) => {
  const cart = getCart(req.sessionID);
  const existing = cart.items.find((i) => i.sku === req.body.sku);
  if (existing) existing.quantity += req.body.quantity ?? 1;
  else cart.items.push({ ...req.body, quantity: req.body.quantity ?? 1 });
  res.status(201).json(cart);
});
`,
  'src/orders.ts': (paidStatus = false) => `
import { randomUUID } from 'node:crypto';
import type { CartItem } from './routes/cart';

export type OrderStatus = 'pending' | ${paidStatus ? "'awaiting_payment' | " : ''}'paid' | 'failed';

export interface Order {
  id: string;
  sessionId: string;
  items: CartItem[];
  status: OrderStatus;
  chargeId?: string;
${paidStatus ? '  paymentIntentId?: string;\n  paidAt?: Date;\n' : ''}}

const orders = new Map<string, Order>();

export function createOrder(sessionId: string, items: CartItem[]): Order {
  const order: Order = { id: randomUUID(), sessionId, items: [...items], status: 'pending' };
  orders.set(order.id, order);
  return order;
}

export function findOrder(id: string) {
  return orders.get(id);
}

export function orderTotal(order: Order) {
  return order.items.reduce((sum, i) => sum + i.unitPrice * i.quantity, 0);
}
${paidStatus ? `
export function markOrderPaid(orderId: string, paymentIntentId: string) {
  const order = orders.get(orderId);
  if (!order || order.status === 'paid') return order;
  order.status = 'paid';
  order.paymentIntentId = paymentIntentId;
  order.paidAt = new Date();
  return order;
}
` : ''}`,
  'src/routes/payments.ts': `
import { Router } from 'express';
import { chargeCard } from '../lib/legacy-billing';
import { getCart, clearCart } from './cart';
import { createOrder, findOrder, orderTotal } from '../orders';

export const paymentRoutes = Router();

// Charges the shopper's card for everything in their cart.
paymentRoutes.post('/checkout', async (req, res) => {
  const cart = getCart(req.sessionID);
  if (cart.items.length === 0) {
    return res.status(400).json({ error: 'Cart is empty' });
  }

  const order = createOrder(req.sessionID, cart.items);
  const amount = orderTotal(order);

  try {
    const charge = await chargeCard({
      token: req.body.cardToken,
      amount,
      currency: 'usd',
      description: \`Order \${order.id}\`,
    });

    if (charge.status !== 'succeeded') {
      order.status = 'failed';
      return res.status(402).json({ error: charge.failureMessage ?? 'Payment declined' });
    }

    order.status = 'paid';
    order.chargeId = charge.id;
    clearCart(req.sessionID);
    res.json({ orderId: order.id, status: order.status });
  } catch (err) {
    console.error('Checkout failed', err);
    res.status(500).json({ error: 'Payment service unavailable' });
  }
});

paymentRoutes.get('/orders/:id', (req, res) => {
  const order = findOrder(req.params.id);
  if (!order || order.sessionId !== req.sessionID) return res.sendStatus(404);
  res.json({ id: order.id, status: order.status, total: orderTotal(order) });
});
`,
  'src/routes/payments.refactored.ts': `
import { Router } from 'express';
import { stripe } from '../lib/stripe';
import { getCart } from './cart';
import { createOrder, findOrder, orderTotal } from '../orders';

export const paymentRoutes = Router();

// Creates a PaymentIntent for the cart. The client confirms it with Stripe.js and the webhook
// marks the order paid, so a dropped connection can never leave a charge unrecorded.
paymentRoutes.post('/checkout', async (req, res) => {
  const cart = getCart(req.sessionID);
  if (cart.items.length === 0) {
    return res.status(400).json({ error: 'Cart is empty' });
  }

  const order = createOrder(req.sessionID, cart.items);

  try {
    const intent = await stripe.paymentIntents.create(
      {
        amount: orderTotal(order),
        currency: 'usd',
        automatic_payment_methods: { enabled: true },
        metadata: { orderId: order.id },
      },
      { idempotencyKey: \`order-\${order.id}\` },
    );

    order.status = 'awaiting_payment';
    order.paymentIntentId = intent.id;
    res.json({ orderId: order.id, clientSecret: intent.client_secret });
  } catch (err) {
    console.error('Could not create PaymentIntent', err);
    res.status(502).json({ error: 'Payment service unavailable' });
  }
});

paymentRoutes.get('/orders/:id', (req, res) => {
  const order = findOrder(req.params.id);
  if (!order || order.sessionId !== req.sessionID) return res.sendStatus(404);
  res.json({ id: order.id, status: order.status, total: orderTotal(order) });
});
`,
  'src/lib/legacy-billing.ts': `
// Thin client for the old in-house billing gateway. Scheduled for removal once checkout
// moves to Stripe PaymentIntents.
export interface ChargeRequest { token: string; amount: number; currency: string; description: string; }
export interface ChargeResult { id: string; status: 'succeeded' | 'declined'; failureMessage?: string; }

export async function chargeCard(req: ChargeRequest): Promise<ChargeResult> {
  const res = await fetch(\`\${process.env.BILLING_URL}/charges\`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(\`Billing gateway returned \${res.status}\`);
  return res.json() as Promise<ChargeResult>;
}
`,
  'src/lib/stripe.ts': (webhookSecret = false, apiVersion = '2024-06-20') => `
import Stripe from 'stripe';

if (!process.env.STRIPE_SECRET_KEY) {
  throw new Error('STRIPE_SECRET_KEY is not set');
}

export const stripe = new Stripe(process.env.STRIPE_SECRET_KEY, {
  apiVersion: '${apiVersion}',
  maxNetworkRetries: 2,
});
${webhookSecret ? `
// Signing secret for the webhook endpoint (whsec_…), from the Stripe dashboard.
export const webhookSecret = process.env.STRIPE_WEBHOOK_SECRET ?? '';
` : ''}`,
  'src/routes/webhooks.ts': `
import express, { Router } from 'express';
import type Stripe from 'stripe';
import { stripe, webhookSecret } from '../lib/stripe';
import { markOrderPaid } from '../orders';

export const webhookRoutes = Router();

webhookRoutes.post('/stripe', express.raw({ type: 'application/json' }), (req, res) => {
  let event: Stripe.Event;
  try {
    event = stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'] as string, webhookSecret);
  } catch (err) {
    console.warn('Rejected webhook with a bad signature', err);
    return res.sendStatus(400);
  }

  switch (event.type) {
    case 'payment_intent.succeeded': {
      const intent = event.data.object as Stripe.PaymentIntent;
      const orderId = intent.metadata.orderId;
      if (orderId) markOrderPaid(orderId, intent.id);
      break;
    }
    case 'payment_intent.payment_failed':
      console.info('Payment failed', event.data.object.id);
      break;
  }

  // Acknowledge quickly; Stripe retries anything that isn't a 2xx.
  res.sendStatus(200);
});
`,
  'web/components/CartSummary.tsx': (redesign = false) => redesign ? `
import { formatPrice } from '../format';
import { PromoCodeInput } from './PromoCodeInput';
import type { CartItem } from '../../src/routes/cart';

interface Props {
  items: CartItem[];
  savings: number;
  onApplyPromo: (code: string) => Promise<boolean>;
}

export function CartSummary({ items, savings, onApplyPromo }: Props) {
  const subtotal = items.reduce((sum, i) => sum + i.unitPrice * i.quantity, 0);
  const total = subtotal - savings;

  return (
    <aside className="cart-summary">
      <h2>Order summary</h2>
      <dl>
        <dt>Subtotal</dt>
        <dd>{formatPrice(subtotal)}</dd>
        {savings > 0 && (
          <>
            <dt>You save</dt>
            <dd className="savings">−{formatPrice(savings)}</dd>
          </>
        )}
        <dt className="total">Total</dt>
        <dd className="total">{formatPrice(total)}</dd>
      </dl>
      <PromoCodeInput onApply={onApplyPromo} />
    </aside>
  );
}
` : `
import { formatPrice } from '../format';
import type { CartItem } from '../../src/routes/cart';

export function CartSummary({ items }: { items: CartItem[] }) {
  const total = items.reduce((sum, i) => sum + i.unitPrice * i.quantity, 0);

  return (
    <aside className="cart-summary">
      <h2>Your cart</h2>
      <ul>
        {items.map((i) => (
          <li key={i.sku}>
            {i.name} × {i.quantity}
          </li>
        ))}
      </ul>
      <span>Total: {formatPrice(total)}</span>
    </aside>
  );
}
`,
  'web/components/CheckoutButton.tsx': (redesign = false) => `
interface Props {
  disabled?: boolean;
  onCheckout: () => void;
}

export function CheckoutButton({ disabled, onCheckout }: Props) {
  return (
    <button className="${redesign ? 'btn btn-primary btn-block' : 'checkout'}" disabled={disabled} onClick={onCheckout}>
      ${redesign ? 'Continue to payment' : 'Checkout'}
    </button>
  );
}
`,
  'web/components/PromoCodeInput.tsx': `
import { useState } from 'react';

export function PromoCodeInput({ onApply }: { onApply: (code: string) => Promise<boolean> }) {
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);

  async function apply() {
    setError(null);
    if (!(await onApply(code.trim()))) setError('That code is not valid.');
  }

  return (
    <div className="promo">
      <input value={code} onChange={(e) => setCode(e.target.value)} placeholder="Promo code" />
      <button className="btn" onClick={apply} disabled={!code.trim()}>Apply</button>
      {error && <p className="error">{error}</p>}
    </div>
  );
}
`,
  'web/format.ts': `
const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });

/** Prices are stored in cents. */
export const formatPrice = (cents: number) => usd.format(cents / 100);
`,
  'tests/payments.test.ts': (refactor = false) => `
import { describe, expect, it, vi } from 'vitest';
${refactor ? "import { stripe } from '../src/lib/stripe';\n" : "import * as billing from '../src/lib/legacy-billing';\n"}
describe('POST /api/payments/checkout', () => {
  it('rejects an empty cart', async () => {
    const res = await checkout({ items: [] });
    expect(res.status).toBe(400);
  });
${refactor ? `
  it('creates a PaymentIntent for the cart total', async () => {
    const create = vi.spyOn(stripe.paymentIntents, 'create').mockResolvedValue({ id: 'pi_1', client_secret: 'cs_1' } as never);
    const res = await checkout({ items: [{ sku: 'mug', name: 'Mug', unitPrice: 1200, quantity: 2 }] });
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ amount: 2400, currency: 'usd' }), expect.anything());
    expect(res.body.clientSecret).toBe('cs_1');
  });

  it('passes the idempotency key through', async () => {
    const create = vi.spyOn(stripe.paymentIntents, 'create').mockResolvedValue({ id: 'pi_2', client_secret: 'cs_2' } as never);
    await checkout({ items: [{ sku: 'mug', name: 'Mug', unitPrice: 1200, quantity: 1 }] });
    expect(create.mock.calls[0][1]).toMatchObject({ idempotencyKey: expect.stringMatching(/^order-/) });
  });
` : `
  it('charges the cart total', async () => {
    const charge = vi.spyOn(billing, 'chargeCard').mockResolvedValue({ id: 'ch_1', status: 'succeeded' });
    await checkout({ items: [{ sku: 'mug', name: 'Mug', unitPrice: 1200, quantity: 2 }] });
    expect(charge).toHaveBeenCalledWith(expect.objectContaining({ amount: 2400 }));
  });
`}});

declare function checkout(cart: unknown): Promise<{ status: number; body: Record<string, string> }>;
`,
  'docs/deploy.md': `
# Deploying to Fly.io

## 1. Create the app

\`\`\`sh
fly launch --no-deploy --name acme-shop
fly postgres create --name acme-shop-db
fly postgres attach acme-shop-db
\`\`\`

## 2. Set secrets

\`\`\`sh
fly secrets set STRIPE_SECRET_KEY=sk_live_… STRIPE_WEBHOOK_SECRET=whsec_… SESSION_SECRET=$(openssl rand -hex 32)
\`\`\`

## 3. Deploy

\`\`\`sh
fly deploy --strategy rolling
\`\`\`

Rolling deploys keep at least one machine serving traffic, so checkout stays up during releases.

## 4. Point Stripe at the webhook

In the Stripe dashboard, add an endpoint for \`https://acme-shop.fly.dev/api/webhooks/stripe\`
listening to \`payment_intent.succeeded\` and \`payment_intent.payment_failed\`.
`,
};

// ------------------------------------------------------------------ history

const remote = path.join(root, 'remote.git');
const main = path.join(root, 'acme-shop');
fs.mkdirSync(main);
execFileSync('git', ['init', '-q', '--bare', '-b', 'main', remote]);
git(main, ['init', '-q', '-b', 'main']);
git(main, ['config', 'core.autocrlf', 'false']);
git(main, ['remote', 'add', 'origin', remote]);

commit(main, 'Initial project scaffold', {
  'package.json': F['package.json']('4.19.2'), 'tsconfig.json': F['tsconfig.json'], 'README.md': F['README.md'](), 'src/server.ts': F['src/server.ts'](),
}, { who: 'maya', daysAgo: 21 });
commit(main, 'Add cart API with in-memory store', { 'src/routes/cart.ts': F['src/routes/cart.ts'] }, { who: 'sam', daysAgo: 18 });
commit(main, 'Add order model and totals', { 'src/orders.ts': F['src/orders.ts']() }, { who: 'sam', daysAgo: 15 });
commit(main, 'Checkout: charge cards through the legacy billing gateway', {
  'src/routes/payments.ts': F['src/routes/payments.ts'], 'src/lib/legacy-billing.ts': F['src/lib/legacy-billing.ts'],
}, { who: 'priya', daysAgo: 12 });
commit(main, 'Add cart summary and checkout button components', {
  'web/components/CartSummary.tsx': F['web/components/CartSummary.tsx'](), 'web/components/CheckoutButton.tsx': F['web/components/CheckoutButton.tsx'](),
  'web/format.ts': F['web/format.ts'],
}, { who: 'leo', daysAgo: 9 });
commit(main, 'Add Stripe client wrapper', { 'src/lib/stripe.ts': F['src/lib/stripe.ts']() }, { who: 'priya', daysAgo: 6 });
commit(main, 'Test the payments route', { 'tests/payments.test.ts': F['tests/payments.test.ts']() }, { who: 'maya', daysAgo: 4 });
commit(main, 'Bump express to 4.21', { 'package.json': F['package.json']('4.21.0') }, { who: 'sam', daysAgo: 2 });
git(main, ['push', '-q', '-u', 'origin', 'main']);

// ------------------------------------------------------------------ branches & worktrees

function branch(dir, name, { pushed = [], local = [] }) {
  git(dir, ['checkout', '-q', '-b', name, 'main']);
  for (const [msg, files, opts] of pushed) commit(dir, msg, files, opts);
  git(dir, ['push', '-q', '-u', 'origin', name]);
  for (const [msg, files, opts] of local) commit(dir, msg, files, opts);
}

// Main checkout: Stripe webhooks (2 outgoing commits).
branch(main, 'feature/stripe-webhooks', {
  pushed: [['Add webhook signing secret to the Stripe config', { 'src/lib/stripe.ts': F['src/lib/stripe.ts'](true) }, { who: 'priya', daysAgo: 1.2 }]],
  local: [
    ['Track awaiting_payment and paid order states', { 'src/orders.ts': F['src/orders.ts'](true) }, { who: 'priya', daysAgo: 0.4 }],
    ['Document STRIPE_WEBHOOK_SECRET in .env.example', {
      '.env.example': 'PORT=3000\nSESSION_SECRET=change-me\nSTRIPE_SECRET_KEY=sk_test_…\nSTRIPE_WEBHOOK_SECRET=whsec_…\n',
    }, { who: 'priya', daysAgo: 0.2 }],
  ],
});

const worktree = (dirName, branchName) => {
  const dir = path.join(root, dirName);
  git(main, ['worktree', 'add', '-q', dir, '-b', branchName, 'main']);
  return dir;
};

const cart = worktree('acme-shop-cart', 'feature/cart-redesign');
commit(cart, 'Extract price formatting helper', { 'web/format.ts': F['web/format.ts'].replace('/** Prices', '/** Formats a price for display. Prices') }, { who: 'leo', daysAgo: 1 });
git(cart, ['push', '-q', '-u', 'origin', 'feature/cart-redesign']);

const docs = worktree('acme-shop-docs', 'docs/deploy-guide');
git(docs, ['push', '-q', '-u', 'origin', 'docs/deploy-guide']);

const payments = worktree('acme-shop-payments', 'refactor/payments');
git(payments, ['push', '-q', '-u', 'origin', 'refactor/payments']);
commit(payments, 'Track awaiting_payment and paid order states', { 'src/orders.ts': F['src/orders.ts'](true) }, { who: 'maya', daysAgo: 0.3 });

// ------------------------------------------------------------------ uncommitted work in each worktree

// checkout-api: staged config + dependency bump, unstaged server change, new webhook route.
write(main, { 'package.json': F['package.json']('4.21.0', '16.8.0'), 'src/lib/stripe.ts': F['src/lib/stripe.ts'](true, '2024-09-30.acacia') });
git(main, ['add', 'package.json', 'src/lib/stripe.ts']);
write(main, { 'src/server.ts': F['src/server.ts'](true), 'src/routes/webhooks.ts': F['src/routes/webhooks.ts'] });

// web-frontend: redesigned components and a new promo-code input.
write(cart, {
  'web/components/CartSummary.tsx': F['web/components/CartSummary.tsx'](true),
  'web/components/CheckoutButton.tsx': F['web/components/CheckoutButton.tsx'](true),
  'web/components/PromoCodeInput.tsx': F['web/components/PromoCodeInput.tsx'],
});

// docs: staged guide, unstaged README link.
write(docs, { 'docs/deploy.md': F['docs/deploy.md'] });
git(docs, ['add', 'docs/deploy.md']);
write(docs, { 'README.md': F['README.md'](true) });

// payments-refactor: route rewritten for PaymentIntents, tests updated, legacy client deleted.
write(payments, { 'src/routes/payments.ts': F['src/routes/payments.refactored.ts'], 'tests/payments.test.ts': F['tests/payments.test.ts'](true) });
fs.rmSync(path.join(payments, 'src/lib/legacy-billing.ts'));

// ------------------------------------------------------------------ fake saved conversations (Resume dialog)

const claudeConfig = path.join(root, 'claude-config');
const encode = (dir) => dir.replace(/[^A-Za-z0-9]/g, '-');
const conversations = [
  [main, 'feature/stripe-webhooks', 'Stripe webhook handler', 'Add a Stripe webhook endpoint that marks orders paid on payment_intent.succeeded', 0.001],
  [payments, 'refactor/payments', 'Refactor payment intents', 'Replace the legacy billing module with Stripe PaymentIntents and keep the tests green', 0.02],
  [cart, 'feature/cart-redesign', 'Cart summary redesign', 'Redesign the cart summary: show savings, add a promo code field', 0.05],
  [docs, 'docs/deploy-guide', 'Deployment guide', 'Write a deployment guide for Fly.io and link it from the README', 0.1],
  [main, 'main', 'Fix flaky cart totals test', 'The cart totals test fails about one run in ten, find out why', 1.6],
  [path.join(root, 'inventory-service'), 'main', 'Low-stock alerts', 'Send a Slack alert when any SKU drops below its reorder point', 2.4],
  [path.join(root, 'inventory-service'), 'chore/pg17', 'Migrate to Postgres 17', 'Upgrade the database to Postgres 17 and check the migrations still apply', 5],
  [path.join(root, 'marketing-site'), 'main', 'Lighthouse performance pass', 'Get the landing page above 95 on Lighthouse mobile', 8],
];
for (const [cwd, gitBranch, title, prompt, daysAgo] of conversations) {
  const id = crypto.randomUUID();
  const dir = path.join(claudeConfig, 'projects', encode(cwd));
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${id}.jsonl`);
  const lines = [
    { type: 'user', sessionId: id, cwd, gitBranch, isSidechain: false, message: { role: 'user', content: prompt } },
    { type: 'ai-title', aiTitle: title, sessionId: id },
    { type: 'last-prompt', lastPrompt: prompt, sessionId: id },
  ];
  fs.writeFileSync(file, lines.map((l) => JSON.stringify(l)).join('\n') + '\n');
  const t = new Date(Date.now() - daysAgo * DAY);
  fs.utimesSync(file, t, t);
}

// ------------------------------------------------------------------ Claude Terminals profile

const fake = path.join(here, 'fake-claude.mjs');
const session = (Id, Name, Directory, scenario, Project = null) => ({ Id, Name, Directory, Command: `node "${fake}" ${scenario}`, Project });
const profile = path.join(root, 'profile');
fs.mkdirSync(profile, { recursive: true });
// Three projects, plus a session with none so the Unknown group shows too.
fs.writeFileSync(path.join(profile, 'projects.json'), JSON.stringify([
  { Id: 'acme', Name: 'Acme Shop', Color: null },
  { Id: 'docs', Name: 'Docs', Color: '#e8a23b' },
  { Id: 'inv', Name: 'Inventory', Color: null },
], null, 2));
fs.writeFileSync(path.join(profile, 'sessions.json'), JSON.stringify([
  session('checkout', 'checkout-api', main, 'checkout', 'acme'),
  session('frontend', 'web-frontend', cart, 'frontend', 'acme'),
  session('payments', 'payments-refactor', payments, 'payments', 'acme'),
  session('docs', 'docs', docs, 'docs', 'docs'),
  session('stock', 'stock-sync', payments, 'payments', 'inv'),
  session('tooling', 'tooling', main, 'docs'),
], null, 2));

console.log(JSON.stringify({ root, profile, claudeConfig }));
