import { Component } from "react";

// Error boundary: if an injected component throws, render nothing rather than breaking the host panel.
export class Safe extends Component<{ children: any }, { err: boolean }> {
    constructor(props: any) {
        super(props);
        this.state = { err: false };
    }
    static getDerivedStateFromError() {
        return { err: true };
    }
    render() {
        return this.state.err ? null : this.props.children;
    }
}
