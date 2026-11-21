// Starts .NET in the page and draws the Game of Life that browser.ty runs. JavaScript only keeps the animation
// loop, the canvas and the input: every generation is computed by life.ty, Python compiled to .NET by Typhon.
import { dotnet } from './_framework/dotnet.js';

const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
const life = exports.WasmLife.JSExports;   // the [JSExport] methods of JSExports.cs, which call browser.ty

const CELL = 8;   // pixels per cell
const canvas = document.getElementById('board');
const ctx = canvas.getContext('2d');
const status = document.getElementById('status');
const runButton = document.getElementById('run');
const patterns = document.getElementById('pattern');
const speed = document.getElementById('speed');

let cells = life.cells();   // a Uint8Array: each cell's age, 0 = dead
let running = false;
let stepMs = 0;

// newborn cells are yellow, cells that have lived longer turn orange, red and violet
const colours = Array.from({ length: 256 }, (_, age) => `hsl(${(50 - Math.min(age, 40) * 4 + 360) % 360} 90% ${age < 2 ? 70 : 58}%)`);

function draw() {
  const w = life.width();
  const h = life.height();
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const age = cells[y * w + x];
      if (age > 0) {
        ctx.fillStyle = colours[age];
        ctx.fillRect(x * CELL + 1, y * CELL + 1, CELL - 1, CELL - 1);
      }
    }
  }
  status.textContent = `generation ${life.generation()} · population ${life.population()}` +
    (stepMs > 0 ? ` · a generation takes ${stepMs.toFixed(2)} ms` : '');
}

function step(generations) {
  const start = performance.now();
  life.step(generations);
  const ms = (performance.now() - start) / generations;
  stepMs = stepMs > 0 ? 0.9 * stepMs + 0.1 * ms : ms;   // a running average: single timings jump around
  cells = life.cells();
  draw();
}

// the animation: as many generations per second as the speed slider says
let last = 0;
function frame(now) {
  // 2 ms early is fine: frames come every ~16.7 ms, give or take a little
  if (running && now - last >= 1000 / Number(speed.value) - 2) {
    last = now;
    step(1);
  }
  requestAnimationFrame(frame);
}

function setRunning(on) {
  running = on;
  runButton.textContent = on ? 'Pause' : 'Start';
}

function redraw() {
  cells = life.cells();
  draw();
}

runButton.onclick = () => setRunning(!running);
document.getElementById('step').onclick = () => { setRunning(false); step(1); };
document.getElementById('random').onclick = () => { life.randomize(Math.floor(Math.random() * 1e9), 0.3); redraw(); };
document.getElementById('clear').onclick = () => { setRunning(false); life.clear(); redraw(); };
document.getElementById('place').onclick = () => { life.place(patterns.value); redraw(); };

for (const name of life.pattern_names()) patterns.add(new Option(name, name));
patterns.value = 'gosper glider gun';

// drawing with the mouse or a finger: the first cell decides whether the stroke draws or erases
let drawing = null;
function paint(event) {
  const rect = canvas.getBoundingClientRect();
  const x = Math.floor((event.clientX - rect.left) / rect.width * canvas.width / CELL);
  const y = Math.floor((event.clientY - rect.top) / rect.height * canvas.height / CELL);
  if (x < 0 || y < 0 || x >= life.width() || y >= life.height()) return;
  drawing ??= cells[y * life.width() + x] === 0;
  life.set_alive(x, y, drawing);
  redraw();
}
canvas.onpointerdown = (event) => { canvas.setPointerCapture(event.pointerId); paint(event); };
canvas.onpointermove = (event) => { if (drawing !== null) paint(event); };
canvas.onpointerup = canvas.onpointercancel = () => { drawing = null; };

canvas.width = life.width() * CELL;
canvas.height = life.height() * CELL;
life.place('gosper glider gun');
redraw();
for (const control of document.querySelectorAll('button, select')) control.disabled = false;
setRunning(true);
requestAnimationFrame(frame);

// runs the __main__ block of browser.ty (its print goes to the console); unlike dotnet.run(), runMain() keeps the
// runtime alive afterwards, so the exports still work
await runMain();
