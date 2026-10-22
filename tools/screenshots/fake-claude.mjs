// Draws a static, Claude Code–style transcript for README screenshots, so no real session,
// account or conversation ever appears in them. Usage: node fake-claude.mjs <scenario>
//   checkout | payments  -> keeps "working" (animated spinner)
//   frontend             -> works for a few seconds, then waits on a permission prompt
//   docs                 -> finished, idle at the prompt

const scenario = process.argv[2] ?? 'checkout';
const out = (s) => process.stdout.write(s);
const ESC = '\x1b[';

const rgb = (r, g, b) => (s) => `${ESC}38;2;${r};${g};${b}m${s}${ESC}39m`;
const bgRgb = (r, g, b) => (s) => `${ESC}48;2;${r};${g};${b}m${s}${ESC}49m`;
const bold = (s) => `${ESC}1m${s}${ESC}22m`;
const orange = rgb(215, 119, 87);
const gray = rgb(153, 153, 153);
const green = rgb(78, 186, 101);
const blue = rgb(177, 185, 249);
const border = rgb(136, 136, 136);
const addBg = bgRgb(34, 74, 40);
const delBg = bgRgb(92, 34, 44);

// Fixed width: Node's process.stdout.columns lags behind ConPTY resizes, and a rule even one column
// too wide wraps. The screenshot terminals are always wider than this.
const cols = () => 100;
const visible = (s) => s.replace(/\x1b\[[0-9;]*m/g, '');
const pad = (s, w) => s + ' '.repeat(Math.max(0, w - visible(s).length));

function box(lines, color = border, width = Math.min(cols(), 76)) {
  const inner = width - 4;
  return [
    color('╭' + '─'.repeat(width - 2) + '╮'),
    ...lines.map((l) => color('│ ') + pad(l, inner) + color(' │')),
    color('╰' + '─'.repeat(width - 2) + '╯'),
  ];
}

const welcome = (cwd) => [
  ...box([`${orange('✻')} Welcome to ${bold('Claude Code')}!`, '', gray(`  cwd: ${cwd}`)], orange, 52),
  '',
];

const you = (text) => [gray('> ') + text, ''];
const say = (text) => ['● ' + text, ''];
const tool = (name, arg, result, body = []) => [
  green('●') + ' ' + bold(name) + `(${arg})`,
  gray('  ⎿  ') + result,
  ...body,
  '',
];
const added = (n, code) => '      ' + gray(String(n).padStart(3)) + ' ' + addBg(pad('+ ' + code, 64));
const removed = (n, code) => '      ' + gray(String(n).padStart(3)) + ' ' + delBg(pad('- ' + code, 64));
const context = (n, code) => '      ' + gray(String(n).padStart(3)) + '   ' + code;

const promptBox = () => {
  const rule = border('─'.repeat(cols()));
  return [rule, gray('> ') + gray('Try "write a test for src/lib/stripe.ts"'), rule, '  ' + gray('⏵⏵ accept edits on (shift+tab to cycle)')];
};

const scenarios = {
  checkout: {
    title: 'Stripe webhook handler',
    lines: [
      ...welcome('~/acme-shop'),
      ...you('Add a Stripe webhook endpoint that marks orders paid on payment_intent.succeeded'),
      ...say("I'll add a webhook route with signature verification and wire it into the server."),
      ...tool('Read', 'src/routes/payments.ts', 'Read 64 lines'),
      ...tool('Write', 'src/routes/webhooks.ts', 'Wrote 41 lines to src/routes/webhooks.ts', [
        added(1, "import express, { Router } from 'express';"),
        added(2, "import { stripe, webhookSecret } from '../lib/stripe';"),
        added(3, "import { markOrderPaid } from '../orders';"),
        gray('       … +38 lines'),
      ]),
      ...tool('Update', 'src/server.ts', 'Updated src/server.ts with 2 additions', [
        context(14, "app.use('/api/cart', cartRoutes);"),
        added(15, "app.use('/api/webhooks', webhookRoutes);"),
      ]),
    ],
    spinner: 'Verifying webhook signatures…',
    tokens: '2.4k',
  },

  payments: {
    title: 'Refactor payment intents',
    lines: [
      ...welcome('~/acme-shop-payments'),
      ...you('Replace the legacy billing module with Stripe PaymentIntents and keep the tests green'),
      ...tool('Update', 'src/routes/payments.ts', 'Updated src/routes/payments.ts with 19 additions and 23 removals', [
        removed(8, "import { chargeCard } from '../lib/legacy-billing';"),
        added(8, "import { stripe } from '../lib/stripe';"),
        gray('       … +40 lines'),
      ]),
      ...tool('Bash', 'npm test -- payments', 'Running…', [
        gray('     ') + green('✓') + ' creates a PaymentIntent for the cart total ' + gray('(41 ms)'),
        gray('     ') + green('✓') + ' rejects an empty cart ' + gray('(3 ms)'),
        gray('     ') + green('✓') + ' passes the idempotency key through ' + gray('(5 ms)'),
      ]),
    ],
    spinner: 'Running the payments test suite…',
    tokens: '5.1k',
  },

  frontend: {
    title: 'Cart summary redesign',
    lines: [
      ...welcome('~/acme-shop-cart'),
      ...you('Redesign the cart summary: show savings, add a promo code field'),
      ...tool('Read', 'web/components/CartSummary.tsx', 'Read 52 lines'),
      ...tool('Write', 'web/components/PromoCodeInput.tsx', 'Wrote 34 lines to web/components/PromoCodeInput.tsx'),
    ],
    spinner: 'Restyling the order total…',
    tokens: '1.8k',
    workSeconds: 7,
    then: [
      ...box([
        bold('Edit file'),
        gray('web/components/CartSummary.tsx'),
        '',
        delBg(pad('- <span>Total: {formatPrice(total)}</span>', 66)),
        addBg(pad('+ <TotalRow total={total} savings={savings} />', 66)),
        addBg(pad('+ <PromoCodeInput onApply={applyPromo} />', 66)),
        '',
        'Do you want to make this edit to ' + bold('CartSummary.tsx') + '?',
        blue('❯ 1. Yes'),
        '  2. Yes, allow all edits during this session ' + gray('(shift+tab)'),
        '  3. No, and tell Claude what to do differently ' + gray('(esc)'),
      ], blue, 74),
    ],
  },

  docs: {
    title: 'Deployment guide',
    lines: [
      ...welcome('~/acme-shop-docs'),
      ...you('Write a deployment guide for Fly.io and link it from the README'),
      ...tool('Write', 'docs/deploy.md', 'Wrote 58 lines to docs/deploy.md'),
      ...tool('Update', 'README.md', 'Updated README.md with 4 additions'),
      ...say('Added ' + bold('docs/deploy.md') + ' covering secrets, the Postgres attachment and zero-downtime deploys,'),
      '  and linked it from the README under "Deploying". Ready for review.',
      '',
    ],
    done: true,
  },
};

const s = scenarios[scenario];
if (!s) {
  console.error(`Unknown scenario "${scenario}". Use: ${Object.keys(scenarios).join(', ')}`);
  process.exit(1);
}

// ------------------------------------------------------------------ the conversation rail
// With the demo's Claude config folder as the second argument, the session registers itself the way Claude Code does
// (<config>/sessions/<pid>.json) and saves its conversation, so the app's conversation rail and tab have one to show.
// Earlier turns come first; the last is the one on screen.

const earlier = {
  checkout: [
    ['Add a /api/cart/totals endpoint that returns subtotal, tax and shipping',
      "Added **`GET /api/cart/totals`** in `src/routes/cart.ts`. It returns:\n\n| Field | From |\n|---|---|\n| `subtotal` | line items × quantity |\n| `tax` | the shipping address's rate |\n| `shipping` | free over $50, otherwise the flat rate |\n\nAll amounts are integers in cents, so nothing is lost to floating point.",
      [['Read', 'src/routes/cart.ts'], ['Update', 'src/routes/cart.ts'], ['Bash', 'npm test -- cart']]],
    ['Tax is off by a cent for some carts. Round per line item instead of on the total',
      "Found it: tax was rounded once on the whole cart, so carts with many small items drifted by a cent. It's now rounded **per line item**, which matches how the payment provider itemises receipts.\n\n- `src/lib/tax.ts`: `taxFor(line)` rounds each line\n- `src/routes/cart.ts`: totals add up the rounded lines",
      [['Read', 'src/lib/tax.ts'], ['Update', 'src/lib/tax.ts'], ['Update', 'src/routes/cart.ts']]],
    ['Write tests for the rounding edge cases',
      'Added 6 cases to `tests/tax.test.ts`, including a cart of 40 items at $0.15 and a 7.25% rate. All pass:\n\n```\nTax rounding\n  ✓ rounds each line, not the total\n  ✓ keeps whole-cent totals for 40 × $0.15\n  ✓ handles a 0% rate\n```',
      [['Write', 'tests/tax.test.ts'], ['Bash', 'npm test -- tax']]],
    ['Use the Stripe test keys from .env.example in the README setup steps',
      'The README\'s *Getting started* now copies `.env.example` to `.env` and explains that the `sk_test_…` keys in it only ever hit Stripe\'s test mode.',
      [['Read', '.env.example'], ['Update', 'README.md']]],
    ['Show the tax line on the order confirmation email',
      'The confirmation email now lists **Subtotal**, **Tax** and **Shipping** above the total, using the same rounded amounts as the cart.',
      [['Read', 'src/email/order-confirmation.tsx'], ['Update', 'src/email/order-confirmation.tsx']]],
  ],
};

function saveConversation(config) {
  const fs = process.getBuiltinModule('node:fs');
  const path = process.getBuiltinModule('node:path');
  const crypto = process.getBuiltinModule('node:crypto');
  const cwd = process.cwd();
  const sessionId = crypto.randomUUID();
  const encoded = path.resolve(cwd).replace(/[\\/]+$/, '').replace(/[^A-Za-z0-9]/g, '-');
  const dir = path.join(config, 'projects', encoded);
  fs.mkdirSync(dir, { recursive: true });
  fs.mkdirSync(path.join(config, 'sessions'), { recursive: true });

  const prompt = s.lines.map(visible).find((l) => l.startsWith('> '))?.slice(2) ?? s.title;
  const turns = [...(earlier[scenario] ?? []), [prompt, null, []]];
  const start = Date.now() - turns.length * 11 * 60_000;   // one prompt every eleven minutes or so
  const entries = [];
  turns.forEach(([text, reply, tools], i) => {
    const timestamp = new Date(start + i * 11 * 60_000).toISOString();
    entries.push({ type: 'user', sessionId, cwd, timestamp, isSidechain: false, message: { role: 'user', content: text } });
    const content = tools.map(([name, arg]) => ({ type: 'tool_use', id: `t${i}${name}`, name, input: name === 'Bash' ? { command: arg } : { file_path: arg } }));
    if (reply) content.push({ type: 'text', text: reply });
    if (content.length) entries.push({ type: 'assistant', sessionId, cwd, timestamp, isSidechain: false, message: { role: 'assistant', content } });
  });
  fs.writeFileSync(path.join(dir, `${sessionId}.jsonl`), entries.map((e) => JSON.stringify(e)).join('\n') + '\n');
  fs.writeFileSync(path.join(config, 'sessions', `${process.pid}.json`),
    JSON.stringify({ pid: process.pid, sessionId, cwd, kind: 'interactive' }));
}

if (process.argv[3]) saveConversation(process.argv[3]);

const tailRow = s.lines.length + 1;   // 1-based row where the spinner / prompt area starts
const spinFrames = ['·', '✢', '✳', '✶', '✻', '✽', '✻', '✶', '✳', '✢'];
const started = Date.now() - 37_000;  // look like it has been working for a while
let frame = 0;
let phase = s.done ? 'done' : 'working';

function spinnerLine() {
  const secs = Math.floor((Date.now() - started) / 1000);
  const elapsed = secs >= 60 ? `${Math.floor(secs / 60)}m ${secs % 60}s` : `${secs}s`;
  return orange(spinFrames[frame++ % spinFrames.length] + ' ' + s.spinner) + ' ' +
    gray(`(esc to interrupt · ${elapsed} · ↓ ${s.tokens} tokens)`);
}

function tail() {
  if (phase === 'done') return promptBox();
  if (phase === 'waiting') return s.then;
  return [spinnerLine(), '', ...promptBox()];
}

const drawTail = () => out(`${ESC}${tailRow};1H${ESC}J` + tail().join('\r\n'));

// Full redraw: on start and whenever the terminal is resized (widths depend on the column count).
function drawAll() {
  out('\x1b[?25l' + `${ESC}2J${ESC}3J${ESC}H` + `\x1b]0;✳ ${s.title}\x07`);
  out(s.lines.join('\r\n') + '\r\n');
  drawTail();
}

drawAll();
process.stdout.on('resize', drawAll);

if (phase === 'working') {
  const timer = setInterval(drawTail, 120);
  if (s.workSeconds) {
    setTimeout(() => {
      clearInterval(timer);
      phase = 'waiting';
      drawTail();
    }, s.workSeconds * 1000);
  }
}

// Stay alive (and quiet) until the app closes the session.
setInterval(() => {}, 1 << 30);
