import { useState, useRef } from 'react';
import { useEffect } from 'react';
import './App.css'

function App() {
  const [hashInput, setHashInput] = useState("");
  const [result, setResult] = useState("");
  const [isLoading, setIsLoading] = useState(false);
  const [progress, setProgress] = useState({ checks: 0, speed: 0, currentLength: 0 });

  const [pepperPlacement, setPepperPlacement] = useState("before");
  const [charOptions, setCharOptions] = useState({
    numbers: true, 
    smallLetters: true, 
    bigLetters: false,
    specialChars: false
  });

  const abortControllerRef = useRef<AbortController | null>(null);

  const handleCheckboxChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const { name, checked } = e.target;
    setCharOptions(prev => ({
      ...prev,
      [name]: checked
    }));
  };

  useEffect(() => {
    let intervalId: any;

    if (isLoading) {
        // Start polling every 1 second
        intervalId = setInterval(async () => {
            try {
                const res = await fetch('http://localhost:5055/api/progress');
                if (res.ok) {
                    const data = await res.json();
                    setProgress(data); // Update state with new numbers
                }
            } catch (err) {
                console.error("Failed to fetch progress", err);
            }
        }, 1000);
    }

    // Cleanup: Stop polling when loading finishes or component unmounts
    return () => {
        if (intervalId) clearInterval(intervalId);
    };
}, [isLoading]); // This effect runs whenever 'isLoading' changes

  const crackHash = async () => {

    const cleanHash = hashInput.trim();

    console.log("Cracking hash:", cleanHash, cleanHash.length);

    const sha256Regex = /^[a-fA-F0-9]{64}$/;

    if (!cleanHash) {
        alert("Please enter a hash!");
        return;
    }

    if (!sha256Regex.test(cleanHash)) {
        alert("Invalid Hash! A SHA-256 hash must be exactly 64 hexadecimal characters.");
        return;
    }

    if (!Object.values(charOptions).some(value => value === true)) {
        alert("Please select at least one character set!");
        return;
    }

    setIsLoading(true);
    setResult("Comparing hashes...");
    setProgress({ checks: 0, speed: 0, currentLength: 0 }); // Reset at start

    abortControllerRef.current = new AbortController();

    try {
      const response = await fetch('http://localhost:5055/api/crack', { 
        method: 'POST', 
        headers: { 'Content-Type': 'application/json' },
        
        body: JSON.stringify({
            hashToCrack: cleanHash, 
            minLength: 4,
            
            pepperLocation: pepperPlacement,
            useNumbers: charOptions.numbers,
            useSmallLetters: charOptions.smallLetters,
            useBigLetters: charOptions.bigLetters,
            useSpecialChars: charOptions.specialChars
        }),

        signal: abortControllerRef.current.signal 
      });

      if (response.ok) {
          const data = await response.json();
          setResult(data.password);
      } else if (response.status === 204) {
          setResult("Operation Cancelled.");
      }

    } catch (error: any) {
      if (error.name === 'AbortError') {
        setResult("Cancelled by user.");

        // --- THE FIX IS HERE ---
        // Force the display back to 0 immediately
        setProgress({ checks: 0, speed: 0, currentLength: 0 });
      } else {
        console.error("Error:", error);
        setResult("Error connecting.");
      }
    } finally {
      setIsLoading(false);
    }
  };

  const cancelCrack = () => {
    if (abortControllerRef.current) {
        abortControllerRef.current.abort();
    }
  };

  return (
    <div style={{ padding: "40px", fontFamily: "Arial, sans-serif" }}>
        <h1>Brute Force Tool</h1>

        {!isLoading ? (
          <div style={{ marginBottom: "20px", textAlign: "left", display: "inline-block" }}>
            
            {/* PEPPER PLACEMENT FORM */}
            <div style={{ marginBottom: "20px", border: "1px solid #ddd", padding: "15px", borderRadius: "8px" }}>
              <h3 style={{ marginTop: 0 }}>Pepper placement:</h3>
              <form>
                <label style={{ display: "block", marginBottom: "5px" }}>
                    <input 
                        type='radio' 
                        name="pepper" 
                        value="before" 
                        checked={pepperPlacement === "before"}
                        onChange={(e) => setPepperPlacement(e.target.value)}
                    /> At the beginning
                </label>
                <label style={{ display: "block" }}>
                    <input 
                        type='radio' 
                        name="pepper" 
                        value="after" 
                        checked={pepperPlacement === "after"}
                        onChange={(e) => setPepperPlacement(e.target.value)}
                    /> At the end
                </label>
              </form>
            </div>

            {/* CHARACTER SET FORM */}
            <div style={{ marginBottom: "20px", border: "1px solid #ddd", padding: "15px", borderRadius: "8px" }}>
              <h3 style={{ marginTop: 0 }}>Character Sets to Use:</h3>
              <form>
                <label style={{ display: "block", marginBottom: "5px" }}>
                    <input 
                        type='checkbox' 
                        name="numbers"
                        checked={charOptions.numbers}
                        onChange={handleCheckboxChange}
                    /> Numbers (0-9)
                </label>
        
                <label style={{ display: "block", marginBottom: "5px" }}>
                    <input 
                        type='checkbox' 
                        name="smallLetters"
                        checked={charOptions.smallLetters}
                        onChange={handleCheckboxChange}
                    /> Small letters (a-z)
                </label>
        
                <label style={{ display: "block", marginBottom: "5px" }}>
                    <input 
                        type='checkbox' 
                        name="bigLetters"
                        checked={charOptions.bigLetters}
                        onChange={handleCheckboxChange}
                    /> Big letters (A-Z)
                </label>
        
                <label style={{ display: "block" }}>
                    <input 
                        type='checkbox' 
                        name="specialChars"
                        checked={charOptions.specialChars}
                        onChange={handleCheckboxChange}
                    /> Special Characters (!@#$...)
                </label>
              </form>
            </div>
          </div>
        ) : null}

        <div>
            <h2>Enter Hash to Crack:</h2>
            <textarea 
                value={hashInput}
                onChange={(e) => setHashInput(e.target.value)}
                disabled={isLoading} 
                rows={3}
                style={{ width: "300px", padding: "10px" }}
            />
            <br/><br/>
            
            {!isLoading ? (
                <button onClick={crackHash} style={{ padding: "10px 20px", fontSize: "16px", cursor: "pointer" }}>
                    Start Cracking
                </button>
            ) : (
                <button onClick={cancelCrack} style={{ padding: "10px 20px", fontSize: "16px", backgroundColor: "red", color: "white", cursor: "pointer" }}>
                    Stop / Cancel
                </button>
            )}      
        </div>
      
        <br/>
        <div>
            <div className="tenor-gif-embed" data-postid="25236336" data-share-method="host" data-aspect-ratio="1" data-width="100%"><a href="https://tenor.com/view/csgo-knife-tricks-sausage-flip-hotdog-flip-gif-25236336">Csgo Knife Tricks GIF</a>from <a href="https://tenor.com/search/csgo-gifs">Csgo GIFs</a></div>
            <script type="text/javascript" async src="https://tenor.com/embed.js"></script>
        </div>

        <div style={{ marginTop: "30px" }}>
            <h3>Result:</h3>
            <p style={{ fontSize: "18px", fontWeight: "bold", color: "#2ce739ff" }}>{result}</p>
        </div>

        {isLoading && (
        <div style={{ marginTop: "20px", padding: "15px", borderRadius: "8px" }}>
            <h3>Crunching Data...</h3>
            <p><strong>Checked:</strong> {progress.checks.toLocaleString()}</p>
            <p><strong>Speed:</strong> {Math.floor(progress.speed).toLocaleString()} Hashes/sec</p>
            <p><strong>Current Length:</strong> {progress.currentLength.toLocaleString()}</p>
        </div>
        )}
    </div>
  )
}

export default App