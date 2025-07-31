import { useState } from 'react'


import './App.css'
import {api, type CodeExecutionRequest} from "./api/apiCode.ts";
import {Button, Select, Textarea, Option} from "@mui/joy";

function App() {

    const [output, setOutput] = useState<string>();
    const [code, setCode] = useState<string>();

    const handleKeyDown = (e) => {
        if (e.key === 'Tab') {
            e.preventDefault();
            const { selectionStart, selectionEnd } = e.target;
            const newText =
                code.substring(0, selectionStart) +
                '\t' +
                code.substring(selectionEnd);
            setCode(newText);
            e.target.selectionStart = e.target.selectionEnd = selectionStart + 1;
        }
    };

    return (
        <>
            <form className={'form'} onSubmit={ async (e: React.FormEvent<HTMLFormElement>) => {
                e.preventDefault();
                const formData = new FormData(e.currentTarget);
                const data = Object.fromEntries((formData as any).entries());
                const rq : CodeExecutionRequest = {code: data.code, language: data.language, correlationId: "string"}
                const response = await api(rq);
                console.log(response)
                if (!response.error){
                    setOutput(response.output);
                }
                else {
                    setOutput(response.error);
                }
            }}>
                <Textarea name={"code"} minRows={15} size={"lg"} variant={"soft"} sx={{
                    width: "100%",
                }} required onChange={(e) => setCode(e.target.value)} onKeyDown={handleKeyDown}></Textarea>
                <Select name={"language"} defaultValue={"python"} required>
                    <Option value={"python"}>Python</Option>
                    <Option value={"csharp"}>C#</Option>
                    <Option value={"javascript"}>JavaScript</Option>
                    <Option value={"c++"}>C++</Option>
                </Select>
                <Button type="submit">Submit</Button>
            </form>
            <div>
                {output ? output : ""}
            </div>
        </>
    )
}

export default App
