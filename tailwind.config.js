/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./**/*.{html,razor,cshtml,css}",
    "./Components/**/*.{razor,html}",
    "./Pages/**/*.{razor,html}",
  ],
  theme: {
    extend: {
      colors: {
        'app': {
          'bg-primary': '#1e1e1e',
          'bg-secondary': '#252526',
          'bg-panel': '#2d2d30',
          'bg-hover': '#37373d',
          'text-primary': '#cccccc',
          'text-secondary': '#9d9d9d',
          'text-muted': '#6a6a6a',
          'border': '#3e3e42',
          'accent': '#007acc',
        }
      },
      animation: {
        'fade-in': 'fadeIn 0.3s ease-in-out',
        'slide-in': 'slideIn 0.3s ease-out',
        'pulse-once': 'pulse 0.5s ease-in-out',
        'scale-in': 'scaleIn 0.2s ease-out',
      },
      keyframes: {
        fadeIn: {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
        slideIn: {
          '0%': { transform: 'translateY(-10px)', opacity: '0' },
          '100%': { transform: 'translateY(0)', opacity: '1' },
        },
        scaleIn: {
          '0%': { transform: 'scale(0.95)', opacity: '0' },
          '100%': { transform: 'scale(1)', opacity: '1' },
        },
      },
    },
  },
  plugins: [],
}
