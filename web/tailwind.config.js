/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        canvas: '#202020',
        panel: '#202020',
        tile: '#282828',
        card: '#282828',
        subtle: '#2a2a2a',
        hairline: 'rgba(255, 255, 255, 0.08)',
      },
      fontFamily: {
        sans: ['-apple-system', 'BlinkMacSystemFont', 'Segoe UI', 'Roboto', 'Inter', 'sans-serif'],
        mono: ['JetBrains Mono', 'Geist Mono', 'Consolas', 'monospace'],
      },
      borderRadius: {
        DEFAULT: '8px',
        lg: '10px',
        xl: '12px',
      }
    },
  },
  plugins: [],
}
