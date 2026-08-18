import fs from "node:fs";
import vm from "node:vm";

const [sourcePath, outputPath] = process.argv.slice(2);

if (!sourcePath || !outputPath) {
  throw new Error("Usage: node tools/export-guest-kiosk.mjs <worker/index.js> <output.html>");
}

const workerSource = fs.readFileSync(sourcePath, "utf8");
const executableSource = workerSource.replace(
  /export default\s*\{[\s\S]*$/,
  "globalThis.__guestKioskHtml = html;"
);

const context = { console };
vm.createContext(context);
vm.runInContext(executableSource, context, { filename: sourcePath });

if (typeof context.__guestKioskHtml !== "string" || !context.__guestKioskHtml.includes("Surge Guest Information Kiosk")) {
  throw new Error("The source did not produce the expected kiosk HTML.");
}

fs.writeFileSync(outputPath, context.__guestKioskHtml, "utf8");
console.log(`Exported ${context.__guestKioskHtml.length} characters to ${outputPath}`);
