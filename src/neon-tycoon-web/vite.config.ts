import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
export default defineConfig({
  plugins:[react(),tailwindcss()],
  server:{proxy:{'/api':'http://localhost:8080'}},
  build:{rollupOptions:{output:{manualChunks(id){
    if(id.includes('node_modules/motion/'))return 'motion';
    if(id.includes('node_modules/@tanstack/'))return 'query';
    if(id.includes('node_modules/react')||id.includes('node_modules/scheduler'))return 'react';
  }}}}
});
