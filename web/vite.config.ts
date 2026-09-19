import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In development the console runs on Vite and forwards API calls to a broker on
// :5310. `npm run build` writes into the API's wwwroot, which serves it.
const broker = process.env.BROKER_URL ?? 'http://localhost:5310';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5320,
    proxy: {
      '/api': broker,
      '/admin': broker,
      '/openapi': broker,
      '/health': broker,
    },
  },
  build: {
    outDir: '../src/OpenFno.Broker.Api/wwwroot',
    emptyOutDir: true,
  },
});
