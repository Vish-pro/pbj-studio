// Builds dist/User-Guide.pdf from README.md "Part 1" (non-coder guide) using Edge headless.
// Run: node packaging/guide/build-guide.mjs   (needs `npm i` once in this folder)
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";
import { marked } from "marked";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..", "..");
const md = readFileSync(path.join(root, "README.md"), "utf8");
const start = md.indexOf("# Part 1");
const end = md.indexOf("# Part 2");
const part1 = md.slice(start, end > start ? end : undefined).replace(/^# Part 1 - /, "# ");
const body = marked.parse(part1);
const logo = pathToFileURL(path.join(root, "website", "images", "logo.png")).href;
const html = `<!doctype html><html><head><meta charset="utf-8"><title>PB&amp;J Studio User Guide: Free PDF Editor and DSC Signer for Windows</title>
<meta name="author" content="vistaar.space"><meta name="description" content="How to install and use PB&amp;J Studio: Multi-Sign with a USB DSC token, Multiple PDFs, rearrange, split, fill, redact and protect.">
<style>
@page{size:A4;margin:18mm 16mm}
body{font:11pt/1.5 "Segoe UI",Arial,sans-serif;color:#111}
img.logo{height:60px;display:block;margin-bottom:6px}
h1{font-size:22pt;margin:.2em 0 .4em;border-bottom:2px solid #111;padding-bottom:.2em}
h2{font-size:15pt;margin:1.3em 0 .4em;break-after:avoid}h3{font-size:12pt;break-after:avoid}
table{border-collapse:collapse;width:100%;margin:.6em 0;font-size:10pt}
td,th{border:1px solid #bbb;padding:4px 8px;vertical-align:top;text-align:left}th{background:#eee}
code{background:#f0f0f0;padding:0 4px;border-radius:3px;font-size:10pt}
tr,li{break-inside:avoid}
.foot{margin-top:2em;font-size:9pt;color:#555;border-top:1px solid #ccc;padding-top:6px}
</style></head><body><img class="logo" src="${logo}">${body}
<div class="foot">PB&amp;J Studio - free - Built by vistaar.space - pdf.truevith.space - Not affiliated with Adobe.</div></body></html>`;
mkdirSync(path.join(root, "dist"), { recursive: true });
const htmlPath = path.join(here, "index.html");
writeFileSync(htmlPath, html);
const out = path.join(root, "dist", "User-Guide.pdf");
const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find((p) => { try { readFileSync(p, { flag: "r" }); return true; } catch { return false; } }) ?? "msedge";
execFileSync(edge, ["--headless", "--disable-gpu", "--no-pdf-header-footer", `--print-to-pdf=${out}`, pathToFileURL(htmlPath).href], { stdio: "inherit" });
console.log("Wrote", out);
