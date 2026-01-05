import { useState, useRef } from 'react';
import './App.css'

function App() {
  const [hashInput, setHashInput] = useState("");
  const [result, setResult] = useState("");
  const [isLoading, setIsLoading] = useState(false);

  const abortControllerRef = useRef<AbortController | null>(null);

  const crackHash = async () => {
    if (!hashInput) return;

    setIsLoading(true);
    setResult("Crunching numbers...");

    abortControllerRef.current = new AbortController();

    try {
      const response = await fetch('http://localhost:5055/api/crack', { 
        method: 'POST', 
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            hashToCrack: hashInput, 
            maxLength: 7
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
    <div style={{ padding: "40px" }}>
        <h1>Brute Force Tool</h1>
        <textarea 
            value={hashInput}
            onChange={(e) => setHashInput(e.target.value)}
            disabled={isLoading} 
        /><br/>
        
        {!isLoading ? (
            <button onClick={crackHash}>Start Cracking</button>
        ) : (

            <button onClick={cancelCrack} style={{ backgroundColor: "red", color: "white" }}>
                Stop / Cancel
            </button>
        )}

        <p>Result: {result}</p>
    </div>
  )
}

export default App