/**
 * MenuScan Voice Soundbox Engine
 * Instant chime & text-to-speech voice announcements for Cashier, Kitchen, and Floor Plan.
 * Supports Hindi (Paytm/PhonePe style) and English voice alerts.
 */
window.MenuScanSoundbox = (function () {
    let enabled = localStorage.getItem('menuscan_soundbox_enabled') !== 'false'; // default true
    let language = localStorage.getItem('menuscan_soundbox_lang') || 'hi'; // 'hi' or 'en'
    let volume = 1.0;
    let audioUnlocked = false;

    // Pleasant electronic chime (similar to Paytm / PhonePe Soundbox)
    function playChime() {
        if (!enabled) return;
        try {
            const AudioCtx = window.AudioContext || window.webkitAudioContext;
            if (!AudioCtx) return;
            const ctx = new AudioCtx();
            const now = ctx.currentTime;

            // Chime notes: C5 (523.25), E5 (659.25), G5 (783.99), C6 (1046.50)
            const notes = [523.25, 659.25, 783.99, 1046.50];
            notes.forEach((freq, idx) => {
                const osc = ctx.createOscillator();
                const gain = ctx.createGain();
                const start = now + (idx * 0.08);
                const dur = 0.28;

                osc.type = "sine";
                osc.frequency.setValueAtTime(freq, start);

                gain.gain.setValueAtTime(0.3 * volume, start);
                gain.gain.exponentialRampToValueAtTime(0.001, start + dur);

                osc.connect(gain);
                gain.connect(ctx.destination);

                osc.start(start);
                osc.stop(start + dur);
            });
        } catch (e) {
            console.warn("Soundbox chime autoplay restriction:", e);
        }
    }

    // Voice announcement via native Web Speech API
    function speak(text, delayMs = 380) {
        if (!enabled) return;
        if (!('speechSynthesis' in window)) return;

        setTimeout(() => {
            try {
                window.speechSynthesis.cancel();
                const utterance = new SpeechSynthesisUtterance(text);
                utterance.volume = volume;
                utterance.rate = 1.0;
                utterance.pitch = 1.05;

                const voices = window.speechSynthesis.getVoices();
                if (language === 'hi') {
                    utterance.lang = 'hi-IN';
                    const hiVoice = voices.find(v => v.lang.startsWith('hi') || v.name.toLowerCase().includes('hindi') || v.name.toLowerCase().includes('india'));
                    if (hiVoice) utterance.voice = hiVoice;
                } else {
                    utterance.lang = 'en-IN';
                    const enVoice = voices.find(v => v.lang === 'en-IN' || v.name.toLowerCase().includes('india') || v.lang.startsWith('en'));
                    if (enVoice) utterance.voice = enVoice;
                }

                window.speechSynthesis.speak(utterance);
            } catch (err) {
                console.warn("Soundbox speech synthesis error:", err);
            }
        }, delayMs);
    }

    // Preload available voices
    if ('speechSynthesis' in window) {
        window.speechSynthesis.onvoiceschanged = () => {
            window.speechSynthesis.getVoices();
        };
    }

    // Unlock browser audio context on first user gesture
    function unlockAudio() {
        if (audioUnlocked) return;
        try {
            const AudioCtx = window.AudioContext || window.webkitAudioContext;
            if (AudioCtx) {
                const ctx = new AudioCtx();
                ctx.resume().then(() => {
                    audioUnlocked = true;
                });
            }
        } catch (e) {}
    }
    document.addEventListener('click', unlockAudio, { once: true });
    document.addEventListener('touchstart', unlockAudio, { once: true });

    function updateUiElements() {
        document.querySelectorAll('.soundbox-status-icon').forEach(icon => {
            if (enabled) {
                icon.className = "bi bi-volume-up-fill text-success soundbox-status-icon";
            } else {
                icon.className = "bi bi-volume-mute-fill text-secondary soundbox-status-icon";
            }
        });

        document.querySelectorAll('.soundbox-status-text').forEach(txt => {
            txt.innerText = enabled ? "Soundbox: ON" : "Soundbox: Muted";
        });

        document.querySelectorAll('.soundbox-lang-select').forEach(sel => {
            sel.value = language;
        });

        const toggleBtn = document.getElementById('soundboxToggleBtn');
        if (toggleBtn) {
            toggleBtn.className = enabled ? "btn btn-sm btn-dark rounded-pill px-3 shadow-sm d-flex align-items-center gap-1" 
                                          : "btn btn-sm btn-outline-secondary rounded-pill px-3 shadow-sm d-flex align-items-center gap-1";
        }
    }

    return {
        isEnabled: () => enabled,
        getLanguage: () => language,

        setEnabled: function (val) {
            enabled = !!val;
            localStorage.setItem('menuscan_soundbox_enabled', enabled ? 'true' : 'false');
            updateUiElements();
            if (enabled) {
                unlockAudio();
                playChime();
            }
        },

        setLanguage: function (lang) {
            language = lang === 'en' ? 'en' : 'hi';
            localStorage.setItem('menuscan_soundbox_lang', language);
            updateUiElements();
            unlockAudio();
            if (enabled) {
                const prompt = language === 'hi' ? "हिन्दी भाषा चुनी गई है।" : "English language selected.";
                speak(prompt, 100);
            }
        },

        toggle: function () {
            this.setEnabled(!enabled);
        },

        announcePayment: function (tableNumber, amount, method = 'UPI') {
            unlockAudio();
            playChime();
            const cleanAmt = Math.round(Number(amount) || 0);
            if (language === 'hi') {
                speak(`${tableNumber} par ${cleanAmt} rupaye ka ${method} payment prapt hua!`);
            } else {
                speak(`Payment received! ${cleanAmt} rupees from ${tableNumber} via ${method}.`);
            }
        },

        announceNewOrder: function (tableNumber, amount, itemCount) {
            unlockAudio();
            playChime();
            const cleanAmt = Math.round(Number(amount) || 0);
            if (language === 'hi') {
                speak(`${tableNumber} par naya order aaya hai, ${cleanAmt} rupaye!`);
            } else {
                speak(`New order received on ${tableNumber}, ${cleanAmt} rupees.`);
            }
        },

        announceServiceCall: function (tableNumber, requestType) {
            unlockAudio();
            playChime();
            if (language === 'hi') {
                if (requestType === 'UPI Payment' || requestType === 'UPI Verification') {
                    speak(`${tableNumber} par UPI payment verify karein!`);
                } else if (requestType === 'Bill') {
                    speak(`${tableNumber} ne bill request kiya hai!`);
                } else if (requestType === 'Water') {
                    speak(`${tableNumber} ne paani request kiya hai!`);
                } else {
                    speak(`Dhyaan dein! ${tableNumber} ne service request kiya hai!`);
                }
            } else {
                if (requestType === 'UPI Payment' || requestType === 'UPI Verification') {
                    speak(`Please verify UPI payment for ${tableNumber}!`);
                } else {
                    speak(`Attention! ${tableNumber} requested ${requestType}.`);
                }
            }
        },

        announceFoodReady: function (tableNumber) {
            unlockAudio();
            playChime();
            if (language === 'hi') {
                speak(`${tableNumber} ka khana tayar hai, jaldi serve karein!`);
            } else {
                speak(`Food is ready for ${tableNumber}! Please dispatch.`);
            }
        },

        testSoundbox: function () {
            unlockAudio();
            playChime();
            if (language === 'hi') {
                speak(`मेनूस्कैन साउंडबॉक्स: ऑडियो अलर्ट ठीक से काम कर रहा है!`);
            } else {
                speak(`MenuScan Soundbox: Voice alerts are working perfectly!`);
            }
        },

        initUi: function () {
            updateUiElements();
        }
    };
})();

document.addEventListener('DOMContentLoaded', function () {
    window.MenuScanSoundbox.initUi();
});
