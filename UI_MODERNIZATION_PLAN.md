# UI Modernization Plan - Implementation Guide

## Current Status
- ✅ Tailwind CSS installed (v3.4.17)
- ✅ Tailwind config created (`tailwind.config.js`)
- ✅ Input CSS created (`wwwroot/css/tailwind-input.css`)
- ✅ Build scripts added to `package.json`

## To Build Tailwind CSS

Run these commands in your terminal:

```powershell
# Install dependencies (downgrade from v4 to v3)
cd c:\GitHub\LiveMonitor
npm install

# Build the CSS
npm run build:css
```

This will generate `wwwroot/css/tailwind.css` (~100KB minified).

## Next Steps After Building

1. **Add Tailwind to index.html**:
   ```html
   <link href="css/tailwind.css" rel="stylesheet" />
   ```

2. **Start using utility classes** in components:
   - Replace `class="app-card"` with `app-card` (from tailwind-input.css)
   - Use standard Tailwind classes like `p-4`, `rounded-lg`, `hover:shadow-lg`, etc.

3. **Commit changes**:
   ```powershell
   git add -A
   git commit -m "feat: Add Tailwind CSS for UI modernization"
   ```

## Implementation Phases

### Phase 1: Foundation ✅
- [x] Add Tailwind config
- [x] Create input CSS with custom components
- [ ] Build CSS (run `npm run build:css`)
- [ ] Add to index.html

### Phase 2: Core Components
- [ ] Create SkeletonLoader.razor
- [ ] Create PageTransition.razor
- [ ] Create AnimatedCounter.razor
- [ ] Update StatCard.razor

### Phase 3: Navigation & Layout
- [ ] Add collapsible sidebar
- [ ] Add animations to MainLayout
- [ ] Add breadcrumbs

### Phase 4: Interactive Elements
- [ ] Enhance buttons
- [ ] Improve modals
- [ ] Update dropdowns

### Phase 5: Data Visualization
- [ ] Add chart animations
- [ ] Enhance stat displays
- [ ] Improve progress indicators
