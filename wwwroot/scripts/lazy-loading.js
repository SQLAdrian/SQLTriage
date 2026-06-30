// Lazy loading intersection observer
export function createIntersectionObserver(dotNetRef, rootMargin) {
    const observer = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            dotNetRef.invokeMethodAsync('OnIntersection', entry.isIntersecting);
        });
    }, {
        rootMargin: `${rootMargin}px`,
        threshold: 0.1
    });

    return {
        observe: (element) => observer.observe(element),
        disconnect: () => observer.disconnect()
    };
}