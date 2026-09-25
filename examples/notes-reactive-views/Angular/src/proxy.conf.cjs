const origin = process.env.RUNIC_WEBUI_ORIGIN;
if (!origin) throw new Error("Set RUNIC_WEBUI_ORIGIN to the Notes backend URL before ng serve.");

const devOrigin = process.env.RUNIC_DEV_ORIGIN;

module.exports = {
  "/webui.js": { target: origin, changeOrigin: true },
  ...(devOrigin ? { "/__runic_dev/**": { target: devOrigin, changeOrigin: true } } : {}),
};
