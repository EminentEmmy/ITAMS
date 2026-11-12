// Shared by every page that uses the split photo layout.
export function initSlideshow(intervalMs = 6000) {
    const slides = [...document.querySelectorAll('.login-carousel .slide')];
    const dots = [...document.querySelectorAll('.dots .dot')];
    if (slides.length < 2) return;

    let current = 0;
    setInterval(() => {
        slides[current].classList.remove('active');
        dots[current]?.classList.remove('active');
        current = (current + 1) % slides.length;
        slides[current].classList.add('active');
        dots[current]?.classList.add('active');
    }, intervalMs);
}

export function initPasswordToggle(inputId = 'password', buttonId = 'toggle-pw') {
    const input = document.getElementById(inputId);
    const btn = document.getElementById(buttonId);
    btn?.addEventListener('click', () => {
        const show = input.type === 'password';
        input.type = show ? 'text' : 'password';
        btn.setAttribute('aria-pressed', String(show));
        btn.setAttribute('aria-label', show ? 'Hide password' : 'Show password');
    });
}