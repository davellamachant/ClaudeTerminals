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
